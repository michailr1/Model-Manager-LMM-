using Velopack;
using Velopack.Sources;

namespace LocalModelManager.Infrastructure.Updates;

public sealed class ApplicationUpdater
{
    private readonly UpdateManager manager;
    private UpdateInfo? available;
    private VelopackAsset? ready;

    public ApplicationUpdater(string repositoryUrl) : this(new UpdateManager(
        new GithubSource(UpdateSettings.NormalizeRepository(repositoryUrl), null, prerelease: false),
        new UpdateOptions { ExplicitChannel = "win-x64" })) { }

    // Allows tests to exercise the SDK with a real feed and isolated installation.
    public ApplicationUpdater(UpdateManager manager)
    {
        this.manager = manager;
        if (manager.IsInstalled) ready = manager.UpdatePendingRestart;
    }
    public bool IsInstalled => manager.IsInstalled;
    public string CurrentVersion => manager.CurrentVersion?.ToString() ?? "не установлена";
    public string? ReadyVersion => ready?.Version.ToString();

    public async Task<string?> CheckAsync()
    {
        if (!IsInstalled)
            throw new InvalidOperationException("Обновления работают в установленной версии. Сначала запустите Setup.exe из релиза; исходники и bin/Debug не заменяются.");
        if (ready is not null) return ready.Version.ToString();
        available = null;
        available = await manager.CheckForUpdatesAsync();
        return available?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken token)
    {
        if (ready is not null) return;
        var update = available ?? throw new InvalidOperationException("Сначала проверьте наличие новой версии.");
        await manager.DownloadUpdatesAsync(update, progress, token);
        // The SDK verifies size and checksum before returning successfully.
        ready = update.TargetFullRelease;
    }

    public void ApplyAndRestart() => manager.ApplyUpdatesAndRestart(
        ready ?? throw new InvalidOperationException("Нет проверенного обновления для установки."));
}
