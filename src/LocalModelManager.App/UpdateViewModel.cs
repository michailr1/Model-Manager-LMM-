using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using LocalModelManager.Infrastructure.Updates;

namespace LocalModelManager.App;

public sealed class UpdateViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MainViewModel main;
    private readonly UpdateSettingsStore store = new(UpdateSettingsStore.DefaultPath);
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromHours(6) };
    private ApplicationUpdater? updater;
    private bool busy;
    private bool disposed;
    private bool settingsLoaded;
    private string savedRepository = "";
    private bool savedAutomatic = true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string RepositoryUrl { get; set; } = "";
    public bool Automatic { get; set; } = true;
    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
    public string Status { get; private set; } = "Источник обновлений не задан. Откройте настройки обновления.";
    public bool IsIdle => !busy;
    public bool CanRestart => !disposed && !busy && main.IsIdle && updater?.ReadyVersion is not null;
    public ICommand SaveCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand RestartCommand { get; }

    public UpdateViewModel(MainViewModel main)
    {
        this.main = main;
        SaveCommand = new UpdateCommand(Save, () => !disposed && !busy);
        CheckCommand = new UpdateCommand(() => _ = CheckAsync(), () => !disposed && !busy && savedRepository.Length > 0);
        RestartCommand = new UpdateCommand(Restart, () => CanRestart);
        main.PropertyChanged += MainChanged;
        timer.Tick += OnTimer;
        try
        {
            var defaultUrl = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "GitHubRepositoryUrl")?.Value ?? "";
            var settings = store.Load(defaultUrl);
            RepositoryUrl = savedRepository = settings.RepositoryUrl;
            Automatic = savedAutomatic = settings.Automatic;
            settingsLoaded = true;
            if (savedRepository.Length > 0) Status = "Готово к проверке обновлений.";
        }
        catch (Exception ex) { SetStatus($"Ошибка чтения настроек обновления: {ex.Message}"); }
    }

    public async Task StartAsync()
    {
        timer.Start();
        if (settingsLoaded && savedAutomatic && savedRepository.Length > 0) await CheckAsync();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        if (settingsLoaded && savedAutomatic && !busy && updater?.ReadyVersion is null && savedRepository.Length > 0)
            _ = CheckAsync();
    }

    private void Save()
    {
        try
        {
            var settings = new UpdateSettings(UpdateSettings.NormalizeRepository(RepositoryUrl), Automatic);
            if (updater?.ReadyVersion is not null && savedRepository != settings.RepositoryUrl)
                throw new InvalidOperationException("Сначала установите скачанное обновление, затем меняйте источник.");
            store.Save(settings);
            RepositoryUrl = savedRepository = settings.RepositoryUrl;
            savedAutomatic = settings.Automatic;
            settingsLoaded = true;
            Changed(nameof(RepositoryUrl));
            SetStatus("Настройки сохранены. Проверка использует только стабильные GitHub Releases.");
            if (savedAutomatic) _ = CheckAsync();
        }
        catch (Exception ex) { SetStatus($"Настройки не сохранены: {ex.Message}"); }
    }

    private async Task CheckAsync()
    {
        if (busy || disposed) return;
        if (updater?.ReadyVersion is not null)
        {
            SetStatus($"Версия {updater.ReadyVersion} скачана. Можно обновить приложение после завершения текущей операции.");
            return;
        }
        busy = true; Refresh();
        try
        {
            updater = new ApplicationUpdater(savedRepository);
            SetStatus("Проверка GitHub Releases…");
            var version = await updater.CheckAsync();
            if (disposed) return;
            if (version is null) { SetStatus($"Установлена актуальная версия {updater.CurrentVersion}. Проверено {DateTime.Now:HH:mm}."); return; }
            var progress = new Progress<int>(percent =>
            {
                if (!disposed && busy) SetStatus($"Скачивание версии {version}: {percent}%");
            });
            await updater.DownloadAsync(((IProgress<int>)progress).Report, lifetime.Token);
            if (!disposed) SetStatus($"Версия {version} скачана и проверена. Нажмите «Обновить и перезапустить».");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!disposed) SetStatus($"Обновление не выполнено: {ex.Message}"); }
        finally { busy = false; Refresh(); }
    }

    private void Restart()
    {
        if (!CanRestart) return;
        try { updater!.ApplyAndRestart(); }
        catch (Exception ex) { SetStatus($"Не удалось установить обновление: {ex.Message}"); }
    }

    private void MainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsIdle)) Refresh();
    }
    private void SetStatus(string status) { Status = status; Changed(nameof(Status)); }
    private void Refresh() { Changed(nameof(IsIdle)); Changed(nameof(CanRestart)); CommandManager.InvalidateRequerySuggested(); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose()
    {
        disposed = true; timer.Stop(); timer.Tick -= OnTimer;
        main.PropertyChanged -= MainChanged;
        lifetime.Cancel();
    }
    private sealed class UpdateCommand(Action execute, Func<bool> canExecute) : ICommand
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
