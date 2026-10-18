using System.Globalization;
using System.Text.Json;
using LocalModelManager.Core;
using LocalModelManager.Infrastructure.Hardware;
using LocalModelManager.Infrastructure.Processes;

namespace LocalModelManager.Infrastructure.LMStudio;

public sealed record LoadPlan(string ModelKey, string ModelPath, string Identifier, MemoryEstimate Estimate,
    double GpuBudgetGiB, double FreeRamGiB, double RamReserveGiB, DateTimeOffset CreatedAt);
public enum ModelLoadState { Declined, Verified, Unconfirmed }
public sealed record ModelLoadResult(ModelLoadState State, LoadPlan Plan, string Message);

public sealed class ModelLoader
{
    private readonly string executable;
    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run;
    private readonly Func<CancellationToken, Task<GpuMemorySnapshot>> readGpu;
    private readonly Func<SystemMemorySnapshot> readRam;
    public ModelLoader(string executable, ProcessRunner runner) : this(executable, runner.RunAsync,
        new NvidiaHardwareInfoService(runner).GetSingleGpuMemoryAsync, SystemMemoryInfo.Read) { }
    public ModelLoader(string executable, Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run,
        Func<CancellationToken, Task<GpuMemorySnapshot>> readGpu, Func<SystemMemorySnapshot> readRam)
    {
        this.executable = executable; this.run = run; this.readGpu = readGpu; this.readRam = readRam;
    }

    public async Task<ModelLoadResult> LoadAsync(ModelFile file, string repository, MemoryAdvice advice,
        Func<LoadPlan, bool> confirm, Action<string, ProcessResult> report, CancellationToken token)
    {
        var selected = advice.Recommendation ?? throw new InvalidOperationException("Сначала получите рекомендацию памяти M3.");
        if (selected.OffloadPercent is < 0 or > 100 || !advice.Attempts.Contains(selected))
            throw new InvalidOperationException("Некорректная рекомендация. Повторите оценку памяти.");
        new ImportRequest(file, repository, ImportMode.SymbolicLink).Validate();
        async Task<ProcessResult> Execute(string label, string[] args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var result = await run(executable, args, ct);
            report(label, result);
            ct.ThrowIfCancellationRequested();
            if (result.ExitCode != 0) throw new InvalidOperationException($"LMS: {label}, exit={result.ExitCode}\n{LmsOutput.CombinedText(result)}");
            return result;
        }
        async Task CheckCatalog(CancellationToken ct)
        {
            var catalog = await Execute("load preflight: ls --json", ["ls", "--json"], ct);
            if (MemoryAdvisor.ResolveModel(catalog.StandardOutput, repository, file.FileName) != advice.ModelKey)
                throw new InvalidOperationException("Модель в каталоге изменилась. Повторите оценку.");
        }
        async Task CheckEmpty(CancellationToken ct)
        {
            var loaded = await Execute("load preflight: ps --json", ["ps", "--json"], ct);
            using var json = JsonDocument.Parse(LmsOutput.Clean(loaded.StandardOutput));
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() != 0)
                throw new InvalidOperationException("В LMS уже есть загруженные модели. M4 не создаёт второй экземпляр и не выгружает модели автоматически. Проверьте список загрузки.");
        }
        var modelPath = repository + "/" + file.FileName;
        var ratio = (selected.OffloadPercent / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
        string[] BaseArguments() => ["load", modelPath, "--exact", "--local", "--context-length", "65536", "--gpu", ratio, "--parallel", "1", "--yes"];
        LoadPlan plan;
        using (var preflight = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            preflight.CancelAfter(TimeSpan.FromSeconds(60));
            var help = await Execute("load --help", ["load", "--help"], preflight.Token);
            var helpText = LmsOutput.CombinedText(help);
            if (new[] { "--identifier", "--context-length", "--gpu", "--parallel", "--estimate-only", "--yes" }.Any(flag => !helpText.Contains(flag, StringComparison.Ordinal)))
                throw new InvalidOperationException("LMS не подтверждает поддержку безопасной загрузки с заданными параметрами.");
            await CheckCatalog(preflight.Token);
            await CheckEmpty(preflight.Token);
            var estimateResult = await Execute("load preflight: fresh estimate", [.. BaseArguments(), "--estimate-only"], preflight.Token);
            var freshEstimate = MemoryAdvisor.ParseEstimate(estimateResult, advice.ModelKey, selected.OffloadPercent);
            var gpu = await readGpu(preflight.Token);
            var ram = readRam();
            var budget = ValidateResources(freshEstimate, gpu, ram);
            plan = new(advice.ModelKey, modelPath, "lmm-" + Guid.NewGuid().ToString("N"), freshEstimate,
                budget.GpuBudgetGiB, ram.AvailableBytes / 1073741824.0, budget.RamReserveGiB, DateTimeOffset.UtcNow);
        }
        token.ThrowIfCancellationRequested();
        if (!confirm(plan)) return new(ModelLoadState.Declined, plan, "Загрузка отменена пользователем. Команда загрузки не запускалась.");
        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow - plan.CreatedAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("План старше двух минут. Повторите подготовку загрузки.");
        // Recheck after a potentially long confirmation dialog, before allocating memory.
        using (var recheck = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            recheck.CancelAfter(TimeSpan.FromSeconds(60));
            new ImportRequest(file, repository, ImportMode.SymbolicLink).Validate();
            await CheckCatalog(recheck.Token);
            await CheckEmpty(recheck.Token);
            ValidateResources(plan.Estimate, await readGpu(recheck.Token), readRam());
        }
        ProcessResult? loadResult = null;
        string? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            loadResult = await run(executable, [.. BaseArguments(), "--identifier", plan.Identifier], token);
            report("load confirmed / " + plan.Identifier, loadResult);
            if (token.IsCancellationRequested) failure = "Запрошена отмена загрузки.";
            else if (loadResult.ExitCode != 0) failure = $"LMS завершил загрузку с exit={loadResult.ExitCode}.";
        }
        catch (OperationCanceledException) { failure = "Запрошена отмена загрузки. Завершение CLI не гарантирует остановку backend."; }
        catch (Exception ex) { failure = "Команда загрузки не подтверждена: " + ex.Message; }
        // Cancellation of the CLI may leave a backend operation running. Inspect independently.
        try
        {
            using var inspection = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await Execute("load verification: ps --json", ["ps", "--json"], inspection.Token);
            VerifyLoaded(result.StandardOutput, plan);
            if (failure is null && loadResult?.ExitCode == 0)
                return new(ModelLoadState.Verified, plan, $"Модель загружена. Контекст подтверждён: 65 536. Экземпляр: {plan.Identifier}. GPU запрошен: {plan.Estimate.OffloadPercent}%.");
            return new(ModelLoadState.Unconfirmed, plan, $"{failure} При проверке экземпляр уже обнаружен с контекстом 65 536. Автоматическая выгрузка не выполнялась.");
        }
        catch (Exception ex)
        {
            return new(ModelLoadState.Unconfirmed, plan, $"{failure ?? "Команда LMS завершилась, но загрузка не подтверждена."} Проверка: {ex.Message}\nBackend может продолжать загрузку; проверьте список загруженных моделей перед повтором. Автоматическая выгрузка не выполнялась.");
        }
    }

    public static (double GpuBudgetGiB, double RamReserveGiB) ValidateResources(MemoryEstimate estimate, GpuMemorySnapshot gpu, SystemMemorySnapshot ram)
    {
        if (gpu.TotalMiB <= 0 || gpu.FreeMiB < 0 || gpu.FreeMiB > gpu.TotalMiB ||
            ram.TotalBytes == 0 || ram.AvailableBytes > ram.TotalBytes ||
            !double.IsFinite(estimate.GpuGiB) || !double.IsFinite(estimate.TotalGiB) ||
            estimate.GpuGiB < 0 || estimate.TotalGiB <= 0 || estimate.TotalGiB < estimate.GpuGiB)
            throw new InvalidOperationException("Не удалось подтвердить доступные ресурсы для загрузки.");
        var gpuBudget = (gpu.FreeMiB - Math.Max(1024, Math.Ceiling(gpu.TotalMiB * 0.05))) / 1024.0;
        if (estimate.GpuGiB + 0.005 > gpuBudget)
            throw new InvalidOperationException("Свободной VRAM стало недостаточно с учётом резерва. Повторите оценку M3.");
        var ramReserve = Math.Max(2, ram.TotalBytes / 1073741824.0 * 0.05);
        // Conservative admission policy: the full LMS total must fit in free RAM plus a reserve.
        // This is a resource check, not a new model-memory estimate.
        if (estimate.TotalGiB + 0.005 + ramReserve > ram.AvailableBytes / 1073741824.0)
            throw new InvalidOperationException("Недостаточно свободной RAM для консервативной проверки: нужна полная оценка памяти LMS и резерв RAM. Освободите память; контекст не уменьшается.");
        return (gpuBudget, ramReserve);
    }

    public static void VerifyLoaded(string json, LoadPlan plan)
    {
        using var document = JsonDocument.Parse(LmsOutput.Clean(json));
        var matches = document.RootElement.EnumerateArray().Where(model =>
            model.TryGetProperty("identifier", out var id) && id.GetString() == plan.Identifier).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Ожидаемый экземпляр отсутствует или неоднозначен в lms ps.");
        var model = matches[0];
        if (!model.TryGetProperty("modelKey", out var key) || key.GetString() != plan.ModelKey ||
            !model.TryGetProperty("path", out var path) || path.GetString()?.Replace('\\', '/') != plan.ModelPath ||
            !model.TryGetProperty("deviceIdentifier", out var device) || device.ValueKind != JsonValueKind.Null ||
            !model.TryGetProperty("contextLength", out var context) || !context.TryGetInt32(out var length) || length != MemoryAdvisor.HermesContext)
            throw new InvalidOperationException("LMS не подтвердил выбранную локальную модель и контекст 65 536. Проверьте фактическую конфигурацию.");
    }
}