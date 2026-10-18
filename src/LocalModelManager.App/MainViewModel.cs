using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LocalModelManager.Core;
using LocalModelManager.Infrastructure.Hardware;
using LocalModelManager.Infrastructure.LMStudio;
using LocalModelManager.Infrastructure.Processes;

namespace LocalModelManager.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ProcessRunner runner = new();
    private CancellationTokenSource? operation;
    private IInferenceProvider? provider;
    private ModelFile? selectedFile;
    private readonly Func<ImportRequest, string, bool> confirmImport;
    private readonly Func<LoadPlan, bool> confirmLoad;
    private MemoryAdvice? memoryAdvice;
    private HermesConnection? apiConnection;
    public ICommand ApiCommand { get; }
    public string ApiSummary { get; private set; } = "Проверьте API загруженной модели, чтобы подготовить профиль Hermes.";
    public bool CanExportLocalProfile => IsIdle && apiConnection is not null;
    public bool CanExportLanProfile => IsIdle && apiConnection?.LanVerifiedFromThisPc == true;
    public ICommand LoadCommand { get; }
    public ICommand LoadedCommand { get; }
    public string LoadStatus { get; private set; } = "Сначала оцените память. Загрузка потребует подтверждения.";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string FileName { get; private set; } = "Файл не выбран";
    public string FullPath { get; private set; } = "";
    public string FileSize { get; private set; } = "";
    public string Gpu { get; private set; } = "GPU: обнаружение не выполнено";
    public string LmsPath { get; private set; } = "LMS: обнаружение не выполнено";
    private string configuredPath = "";
    public string ConfiguredPath
    {
        get => configuredPath;
        set { configuredPath = value; ResetMemoryAdvice(); Changed(); }
    }
    public string Output { get; private set; } = "";
    public string Logs { get; private set; } = "";
    private string repository = "";
    public string Repository
    {
        get => repository;
        set { repository = value; ResetMemoryAdvice(); Changed(); }
    }
    public string MemorySummary { get; private set; } = "Выберите подключённый GGUF. Hermes требует контекст 65 536 токенов.";
    public ICommand EstimateCommand { get; }
    public bool CopyMode { get; set; }
    public string ImportStatus { get; private set; } = "Выберите GGUF — категория подставится автоматически.";
    public bool IsIdle => operation is null;
    public ICommand DiscoverCommand { get; }
    public ICommand ListCommand { get; }
    public ICommand StatusCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ImportCommand { get; }

    public MainViewModel(Func<ImportRequest, string, bool> confirmImport, Func<LoadPlan, bool> confirmLoad)
    {
        this.confirmImport = confirmImport;
        this.confirmLoad = confirmLoad;
        ApiCommand = new Command(() => Run(VerifyApi), () => IsIdle && provider is not null);
        LoadCommand = new Command(() => Run(LoadModel, isLoad: true), () => IsIdle && selectedFile is not null && memoryAdvice?.Recommendation is not null);
        LoadedCommand = new Command(() => Run(ShowLoadedModels), () => IsIdle && provider is not null);
        DiscoverCommand = new Command(() => Run(Discover), () => IsIdle);
        ListCommand = new Command(() => Run(t => ExecuteLms(false, t)), () => IsIdle && provider is not null);
        StatusCommand = new Command(() => Run(t => ExecuteLms(true, t)), () => IsIdle && provider is not null);
        CancelCommand = new Command(Cancel, () => !IsIdle);
        EstimateCommand = new Command(() => Run(EstimateMemory), () => IsIdle && provider is not null && selectedFile is not null);
        ImportCommand = new Command(() => Run(Import, isImport: true), () => IsIdle && provider is not null && selectedFile is not null);
    }
    public void Cancel() => operation?.Cancel();
    public void SelectFiles(string[] paths)
    {
        if (!IsIdle) { Log("[file] Дождитесь завершения операции или отмените её."); return; }
        try
        {
            if (paths.Length != 1) throw new ArgumentException("Выберите ровно один GGUF-файл.");
            var file = ModelFile.Read(paths[0]);
            if (string.IsNullOrWhiteSpace(Repository) || Repository.Trim().Equals("local/", StringComparison.OrdinalIgnoreCase) ||
                (selectedFile is not null && Repository == ImportRequest.SuggestRepository(selectedFile.FileName)))
            {
                Repository = ImportRequest.SuggestRepository(file.FileName);
                Changed(nameof(Repository));
            }
            selectedFile = file;
            ResetMemoryAdvice();
            CopyMode = false; Changed(nameof(CopyMode));
            SetImportStatus("Категория готова. Проверьте её и нажмите «Подключить к LM Studio».");
            FileName = file.FileName;
            FullPath = file.FullPath;
            FileSize = $"{file.SizeBytes:N0} bytes ({file.SizeBytes / 1073741824.0:F2} GiB)";
            Changed(nameof(FileName)); Changed(nameof(FullPath)); Changed(nameof(FileSize));
            Log($"[file] {file.FullPath} / {file.SizeBytes} bytes");
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex) { Log($"[error] {ex.Message}"); }
    }
    private async void Run(Func<CancellationToken, Task> action, bool isImport = false, bool isLoad = false)
    {
        if (!IsIdle) return;
        operation = isImport || isLoad ? new CancellationTokenSource() : new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Changed(nameof(IsIdle)); Changed(nameof(CanExportLocalProfile)); Changed(nameof(CanExportLanProfile)); CommandManager.InvalidateRequerySuggested();
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            Log("[cancel] Операция отменена или истекло время ожидания.");
            if (isImport) SetImportStatus("Операция отменена. Если импорт уже начался, проверьте каталог LM Studio: результат может быть частичным. Файлы автоматически не удаляются.");
        }
        catch (Exception ex)
        {
            Log($"[error] {ex.Message}");
            if (isImport) SetImportStatus($"Импорт не подтверждён: {ex.Message}");
        }
        finally
        {
            operation.Dispose(); operation = null;
            Changed(nameof(IsIdle)); Changed(nameof(CanExportLocalProfile)); Changed(nameof(CanExportLanProfile)); CommandManager.InvalidateRequerySuggested();
        }
    }
    private void ResetMemoryAdvice()
    {
        ResetApiVerification();
        memoryAdvice = null;
        LoadStatus = "Сначала оцените память. Загрузка потребует подтверждения.";
        Changed(nameof(LoadStatus));
        CommandManager.InvalidateRequerySuggested();
        MemorySummary = "Выберите подключённый GGUF. Hermes требует контекст 65 536 токенов.";
        Changed(nameof(MemorySummary));
    }
    private async Task EstimateMemory(CancellationToken token)
    {
        memoryAdvice = null;
        CommandManager.InvalidateRequerySuggested();
        MemorySummary = "Проверка свободной VRAM и оценки LM Studio для Hermes 64K…";
        Changed(nameof(MemorySummary));
        try
        {
            var path = LmsDiscovery.Find(ConfiguredPath) ?? throw new InvalidOperationException("LMS не найден.");
            var gpu = await new NvidiaHardwareInfoService(runner).GetSingleGpuMemoryAsync(token);
            Log($"[memory] {gpu.Name}: total={gpu.TotalMiB} MiB, free={gpu.FreeMiB} MiB");
            var advice = await new MemoryAdvisor(path, runner).AdviseAsync(selectedFile!, Repository.Trim(), gpu,
                (label, result) => { Log($"[memory] {label}"); ShowResult(result); }, token);
            memoryAdvice = advice;
            LoadStatus = advice.Recommendation is null ? "Нет подходящей рекомендации для загрузки." : "Рекомендация готова. Перед загрузкой будут повторно проверены VRAM и RAM.";
            Changed(nameof(LoadStatus));
            CommandManager.InvalidateRequerySuggested();
            var first = advice.Attempts[0];
            var summary = $"Модель: {advice.ModelKey}. Hermes: 65 536 токенов.\n" +
                $"Свободно VRAM: {gpu.FreeMiB / 1024.0:F2} GiB; резерв: {advice.ReserveGiB:F2} GiB; бюджет: {advice.BudgetGiB:F2} GiB.\n" +
                $"100% GPU: оценка LMS {first.GpuGiB:F2} GiB. ";
            if (advice.Recommendation is { } selected)
            {
                summary += $"Рекомендация: {selected.OffloadPercent}% GPU, VRAM {selected.GpuGiB:F2} GiB.\n" +
                    $"Оценка всей памяти LMS: {selected.TotalGiB:F2} GiB. ";
                if (selected.OffloadPercent == 0) summary += "ВНИМАНИЕ: подходит только CPU; работа может быть очень медленной. ";
            }
            else summary += "ВНИМАНИЕ: ни одна оценка GPU не укладывается в бюджет. Рекомендации нет. ";
            MemorySummary = summary + "\nЭто оценка без загрузки. Доступность RAM проверяется перед загрузкой; успешная работа ещё не гарантирована.";
            Changed(nameof(MemorySummary));
            Log($"[memory] {MemorySummary}");
        }
        catch (OperationCanceledException)
        {
            MemorySummary = "Оценка отменена или истекло время ожидания. Рекомендация не сформирована.";
            Changed(nameof(MemorySummary));
            throw;
        }
        catch (Exception ex)
        {
            MemorySummary = $"Оценка не подтверждена: {ex.Message}";
            Changed(nameof(MemorySummary));
            throw;
        }
    }
    private async Task LoadModel(CancellationToken token)
    {
        var advice = memoryAdvice ?? throw new InvalidOperationException("Сначала оцените память.");
        memoryAdvice = null;
        CommandManager.InvalidateRequerySuggested();
        ResetApiVerification();
        SetLoadStatus("Подготовка загрузки: свежая оценка LMS, VRAM и RAM…");
        try
        {
            var path = LmsDiscovery.Find(ConfiguredPath) ?? throw new InvalidOperationException("LMS не найден.");
            var result = await new ModelLoader(path, runner).LoadAsync(selectedFile!, Repository.Trim(), advice,
                plan =>
                {
                    Log($"[load] plan / {plan.ModelKey} / context=65536 / GPU={plan.Estimate.OffloadPercent}% / VRAM={plan.Estimate.GpuGiB:F2} GiB / free RAM={plan.FreeRamGiB:F2} GiB");
                    var approved = confirmLoad(plan);
                    Log(approved ? "[load] confirmation accepted" : "[load] confirmation declined");
                    SetLoadStatus(approved ? "Загрузка модели… Это может занять несколько минут. Доступна отмена." : "Загрузка отменена до запуска.");
                    return approved;
                }, (label, response) => { Log($"[load] {label}"); ShowResult(response); }, token);
            SetLoadStatus(result.Message);
            Log($"[load] {result.State}: {result.Message}");
        }
        catch (OperationCanceledException)
        {
            SetLoadStatus("Подготовка загрузки отменена или истекло время ожидания. Проверьте список загруженных моделей перед повтором.");
            throw;
        }
        catch (Exception ex)
        {
            SetLoadStatus($"Загрузка не подтверждена: {ex.Message}");
            throw;
        }
    }
    private async Task ShowLoadedModels(CancellationToken token)
    {
        var path = LmsDiscovery.Find(ConfiguredPath) ?? throw new InvalidOperationException("LMS не найден.");
        Log("[load] ps --json (обновление состояния)");
        var result = await runner.RunAsync(path, ["ps", "--json"], token);
        ShowResult(result);
        if (result.ExitCode != 0)
        {
            SetLoadStatus("Состояние загрузки не подтверждено. Подробности в Logs.");
            throw new InvalidOperationException("Не удалось обновить список загруженных моделей; подробности в Logs.");
        }
        try
        {
            using var document = JsonDocument.Parse(LmsOutput.Clean(result.StandardOutput));
            var models = document.RootElement.EnumerateArray().ToArray();
            SetLoadStatus(models.Length == 0 ? "В LMS нет загруженных моделей." : string.Join(Environment.NewLine, models.Select(model =>
                $"Загружена: {model.GetProperty("modelKey").GetString()}; контекст: {model.GetProperty("contextLength").GetInt32():N0}; статус: {model.GetProperty("status").GetString()}. Экземпляр: {model.GetProperty("identifier").GetString()}")));
        }
        catch
        {
            SetLoadStatus("Не удалось разобрать состояние загрузки; исходный ответ доступен в Результате и Logs.");
            throw;
        }
    }
    private void SetLoadStatus(string text) { LoadStatus = text; Changed(nameof(LoadStatus)); }
    private void ResetApiVerification()
    {
        apiConnection = null;
        ApiSummary = "Проверьте API загруженной модели, чтобы подготовить профиль Hermes.";
        Changed(nameof(ApiSummary)); Changed(nameof(CanExportLocalProfile)); Changed(nameof(CanExportLanProfile));
    }
    private async Task VerifyApi(CancellationToken token)
    {
        ResetApiVerification();
        ApiSummary = "Проверка модели, API и тестового ответа…";
        Changed(nameof(ApiSummary));
        try
        {
            var path = LmsDiscovery.Find(ConfiguredPath) ?? throw new InvalidOperationException("LMS не найден.");
            using var http = HermesApiService.CreateLocalClient();
            var connection = await new HermesApiService(path, runner, http).VerifyAsync(selectedFile, Repository,
                Log, (label, result) => { Log($"[api] {label}"); ShowResult(result); }, token);
            apiConnection = connection;
            ApiSummary = $"API проверен. Ответ: {connection.Reply}\nМодель: {connection.Model.Identifier}; контекст: {connection.Model.ContextLength:N0}.\n" +
                $"На этом ПК: {connection.LocalBaseUrl}\n" +
                (connection.LanVerifiedFromThisPc ? $"По LAN: {connection.LanBaseUrl} ({connection.LanInterface}). Проверено с этого ПК; другое устройство ещё не проверено." : connection.LanWarning);
            Changed(nameof(ApiSummary)); Changed(nameof(CanExportLocalProfile)); Changed(nameof(CanExportLanProfile));
            Log("[api] " + ApiSummary);
        }
        catch (OperationCanceledException)
        {
            ResetApiVerification();
            ApiSummary = "Проверка API отменена или истекло время ожидания. Профиль не подготовлен; сервер и модель автоматически не остановлены.";
            Changed(nameof(ApiSummary)); throw;
        }
        catch (Exception ex)
        {
            ResetApiVerification();
            ApiSummary = "API не подтверждён: " + ex.Message;
            Changed(nameof(ApiSummary)); throw;
        }
    }
    public string ProfileForExport(bool useLan)
    {
        if (!IsIdle || apiConnection is null || DateTimeOffset.UtcNow - apiConnection.VerifiedAt > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Сначала обновите проверку API. Профиль можно экспортировать в течение пяти минут после проверки.");
        return HermesApiService.BuildProfile(apiConnection, useLan);
    }
    public void ReportProfileExport(string path) => Log("[hermes] Профиль сохранён: " + path);
    private async Task Import(CancellationToken token)
    {
        var request = new ImportRequest(selectedFile!, Repository.Trim(), CopyMode ? ImportMode.Copy : ImportMode.SymbolicLink);
        request.Validate();
        SetImportStatus("Проверка плана импорта…");
        Log($"[import] preview / {request.Mode} / {request.Repository} / {request.File.FullPath}");
        ProcessResult preview;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            preview = await provider!.PreviewImportAsync(request, timeout.Token);
        }
        ShowResult(preview);
        if (preview.ExitCode != 0)
        {
            SetImportStatus("Предварительная проверка не прошла. Импорт не запущен; подробности в Logs.");
            return;
        }
        token.ThrowIfCancellationRequested();
        if (!confirmImport(request, LmsOutput.CombinedText(preview)))
        {
            SetImportStatus("Импорт отменён пользователем до изменения файлов.");
            Log("[import] confirmation declined");
            return;
        }
        token.ThrowIfCancellationRequested();
        SetImportStatus(request.Mode == ImportMode.Copy ? "Копирование… Это может занять несколько минут." : "Создание symbolic link…");
        Log($"[import] confirmed / {request.Mode} / {request.File.SizeBytes} bytes");
        var result = await provider!.ImportAsync(request, copyApproved: request.Mode == ImportMode.Copy, token);
        ShowResult(result);
        if (result.ExitCode != 0)
        {
            SetImportStatus("LMS сообщил об ошибке импорта. Автоматического перехода на Copy нет. Проверьте Logs и каталог назначения перед повтором.");
            return;
        }
        var original = ModelFile.Read(request.File.FullPath);
        if (original.SizeBytes != request.File.SizeBytes)
            throw new IOException("После импорта размер исходного файла изменился. Проверьте исходный файл.");
        SetImportStatus("LMS завершил импорт успешно; исходный файл существует, размер сохранён.");
        Log("[import] success; source exists with unchanged size");
        if (request.Mode == ImportMode.SymbolicLink)
            Log("[import] Не перемещайте и не удаляйте исходный GGUF: LM Studio использует ссылку на него.");
        // Listing failure must not turn an already completed import into a reported import failure.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            Log("[lms] refreshing ls after import");
            var list = await provider.ListModelsAsync(timeout.Token);
            ShowResult(list);
            if (list.ExitCode != 0) Log("[warning] Импорт завершён, но обновить список не удалось.");
        }
        catch (Exception ex) { Log($"[warning] Импорт завершён; обновление списка: {ex.Message}"); }
    }
    private void SetImportStatus(string text) { ImportStatus = text; Changed(nameof(ImportStatus)); }

    private void ShowResult(ProcessResult result)
    {
        Output = LmsOutput.CombinedText(result); Changed(nameof(Output));
        Log($"[stdout] {LmsOutput.Clean(result.StandardOutput)}");
        if (!string.IsNullOrWhiteSpace(result.StandardError)) Log($"[stderr] {LmsOutput.Clean(result.StandardError)}");
        Log($"[lms] exit={result.ExitCode}");
    }
    private async Task Discover(CancellationToken token)
    {
        ResetMemoryAdvice();
        var path = LmsDiscovery.Find(ConfiguredPath);
        provider = path is null ? null : new LmsProvider(path, runner);
        LmsPath = path is null ? "LMS не найден. Установите LM Studio или укажите путь к lms.exe." : $"LMS: {path}";
        Changed(nameof(LmsPath)); Log($"[lms] {LmsPath}");
        Gpu = "GPU: обнаружение…"; Changed(nameof(Gpu));
        try
        {
            var gpus = await new NvidiaHardwareInfoService(runner).GetGpusAsync(token);
            Gpu = string.Join(Environment.NewLine, gpus.Select(g => $"{g.Name} / {g.TotalMemoryMiB} MiB ({g.TotalMemoryMiB / 1024.0:F2} GiB)"));
            Log($"[hardware] {Gpu}");
        }
        catch { Gpu = "GPU не определён; подробности в Logs (требуется NVIDIA driver / nvidia-smi)."; throw; }
        finally { Changed(nameof(Gpu)); }
    }
    private async Task ExecuteLms(bool status, CancellationToken token)
    {
        Log(status ? "[lms] server status" : "[lms] ls");
        var result = status ? await provider!.GetServerStatusAsync(token) : await provider!.ListModelsAsync(token);
        Output = LmsOutput.CombinedText(result);
        Changed(nameof(Output));
        Log($"[stdout] {LmsOutput.Clean(result.StandardOutput)}");
        if (!string.IsNullOrWhiteSpace(result.StandardError)) Log($"[stderr] {LmsOutput.Clean(result.StandardError)}");
        Log($"[lms] exit={result.ExitCode}");
        if (status && result.ExitCode == 0) Log($"[server] {LmsOutput.ParseServerState(Output)}");
        if (result.ExitCode != 0) Log("[error] Команда LMS завершилась с ошибкой.");
    }
    private void Log(string text) { Logs += $"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}"; Changed(nameof(Logs)); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    private sealed class Command(Action execute, Func<bool> canExecute) : ICommand
    {
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter) { if (canExecute()) execute(); }
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
