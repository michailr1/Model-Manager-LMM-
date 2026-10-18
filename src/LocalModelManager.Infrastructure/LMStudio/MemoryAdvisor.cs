using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalModelManager.Core;
using LocalModelManager.Infrastructure.Hardware;
using LocalModelManager.Infrastructure.Processes;

namespace LocalModelManager.Infrastructure.LMStudio;

public sealed record MemoryEstimate(int OffloadPercent, double GpuGiB, double TotalGiB);
public sealed record MemoryAdvice(string ModelKey, double BudgetGiB, double ReserveGiB,
    IReadOnlyList<MemoryEstimate> Attempts, MemoryEstimate? Recommendation);

public sealed class MemoryAdvisor
{
    public const int HermesContext = 65536;
    private readonly string executable;
    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run;
    public MemoryAdvisor(string executable, ProcessRunner runner) : this(executable, runner.RunAsync) { }
    public MemoryAdvisor(string executable, Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run)
    {
        this.executable = executable;
        this.run = run;
    }

    public async Task<MemoryAdvice> AdviseAsync(ModelFile file, string repository, GpuMemorySnapshot gpu,
        Action<string, ProcessResult> report, CancellationToken token)
    {
        new ImportRequest(file, repository, ImportMode.SymbolicLink).Validate();
        if (gpu.TotalMiB <= 0 || gpu.FreeMiB < 0 || gpu.FreeMiB > gpu.TotalMiB)
            throw new ArgumentException("Некорректные данные свободной VRAM.");
        var reserveMiB = Math.Max(1024, Math.Ceiling(gpu.TotalMiB * 0.05));
        var budgetGiB = (gpu.FreeMiB - reserveMiB) / 1024.0;
        if (budgetGiB <= 0) throw new InvalidOperationException("Недостаточно свободной VRAM даже для резерва. Освободите GPU и повторите оценку.");
        async Task<ProcessResult> Execute(string label, string[] arguments)
        {
            token.ThrowIfCancellationRequested();
            var result = await run(executable, arguments, token);
            report(label, result);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"LMS: {label}, exit={result.ExitCode}\n{LmsOutput.CombinedText(result)}");
            return result;
        }
        var help = await Execute("load --help", ["load", "--help"]);
        var helpText = LmsOutput.CombinedText(help);
        if (new[] { "--estimate-only", "--context-length", "--gpu", "--yes" }.Any(flag => !helpText.Contains(flag, StringComparison.Ordinal)))
            throw new InvalidOperationException("Эта версия LMS не подтверждает поддержку оценки без загрузки модели.");
        var catalog = await Execute("ls --json", ["ls", "--json"]);
        var modelKey = ResolveModel(catalog.StandardOutput, repository, file.FileName);
        var attempts = new List<MemoryEstimate>();
        for (var percent = 100; percent >= 0; percent -= 5)
        {
            var result = await Execute($"estimate {percent}% / context={HermesContext}",
                ["load", modelKey, "--context-length", HermesContext.ToString(CultureInfo.InvariantCulture),
                    "--gpu", (percent / 100.0).ToString("0.00", CultureInfo.InvariantCulture), "--estimate-only", "--yes"]);
            var estimate = ParseEstimate(result, modelKey, percent);
            attempts.Add(estimate);
            // CLI memory values are rounded to two decimals. Allow for that rounding, not overcommit.
            if (estimate.GpuGiB + 0.005 <= budgetGiB)
                return new(modelKey, budgetGiB, reserveMiB / 1024.0, attempts, estimate);
        }
        return new(modelKey, budgetGiB, reserveMiB / 1024.0, attempts, null);
    }

    public static string ResolveModel(string json, string repository, string fileName)
    {
        using var catalog = JsonDocument.Parse(LmsOutput.Clean(json));
        var expectedPath = repository + "/" + fileName;
        var entries = catalog.RootElement.EnumerateArray().ToArray();
        var matches = entries.Where(entry => entry.TryGetProperty("path", out var path) &&
            string.Equals(path.GetString()?.Replace('\\', '/'), expectedPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Модель не найдена однозначно в каталоге LMS. Проверьте категорию и подключение; после импорта дождитесь обновления списка.");
        var model = matches[0];
        if (!model.TryGetProperty("deviceIdentifier", out var device) || device.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("M3 оценивает только локальную модель; удалённое устройство или его отсутствие в метаданных не поддерживается.");
        if (model.GetProperty("type").GetString() != "llm" || model.GetProperty("format").GetString() != "gguf")
            throw new InvalidOperationException("Для Hermes требуется языковая модель GGUF.");
        if (!model.TryGetProperty("maxContextLength", out var context) || !context.TryGetInt32(out var maxContext))
            throw new InvalidOperationException("LMS не сообщил максимальный контекст модели. Совместимость с Hermes 64K не подтверждена.");
        if (maxContext < HermesContext)
            throw new InvalidOperationException($"Модель сообщает максимум {maxContext:N0} токенов. Hermes требует {HermesContext:N0}; контекст автоматически не уменьшается.");
        var key = model.GetProperty("modelKey").GetString();
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith('-') ||
            entries.Count(entry => entry.GetProperty("modelKey").GetString() == key) != 1)
            throw new InvalidOperationException("Неоднозначный modelKey LMS: безопасный выбор модели невозможен.");
        return key;
    }

    public static MemoryEstimate ParseEstimate(ProcessResult result, string modelKey, int percent)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"Оценка LMS завершилась с exit={result.ExitCode}.");
        var text = LmsOutput.CombinedText(result);
        string Field(string label)
        {
            var matches = Regex.Matches(text, @"^\s*" + Regex.Escape(label) + @":\s*([^\r\n]+)", RegexOptions.Multiline);
            if (matches.Count != 1) throw new FormatException($"Не удалось однозначно прочитать {label} из оценки LMS.");
            return matches[0].Groups[1].Value.Trim();
        }
        if (Field("Model") != modelKey || Field("Context Length").Replace(",", "").Replace(" ", "") != "65536" ||
            Field("GPU Offload") != percent.ToString(CultureInfo.InvariantCulture) + "%")
            throw new FormatException("LMS оценил другую модель, контекст или долю GPU. Рекомендация не сформирована.");
        double Memory(string label)
        {
            var match = Regex.Match(Field(label), @"^([0-9]+(?:\.[0-9]+)?)\s*(GiB|MiB|KiB|GB|MB|KB|B)$");
            if (!match.Success) throw new FormatException($"Неизвестный формат памяти {label}.");
            var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var gib = match.Groups[2].Value switch
            {
                "GiB" => value, "MiB" => value / 1024, "KiB" => value / 1048576,
                "GB" => value * 1e9 / 1073741824, "MB" => value * 1e6 / 1073741824,
                "KB" => value * 1e3 / 1073741824, _ => value / 1073741824
            };
            if (!double.IsFinite(gib)) throw new FormatException("Некорректная оценка памяти LMS.");
            return gib;
        }
        var gpu = Memory("Estimated GPU Memory");
        var total = Memory("Estimated Total Memory");
        if (total <= 0 || total + 0.005 < gpu || (percent > 0 && gpu <= 0))
            throw new FormatException("Оценка памяти LMS неполная или противоречива.");
        return new(percent, gpu, total);
    }
}