using System.Security.Cryptography;
using System.Text.Json;
using LocalModelManager.Infrastructure.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace LocalModelManager.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LmmUpdateTests", Guid.NewGuid().ToString("N"));
    public UpdateTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData("https://github.com/michailr1/Model-Manager-LMM-", "https://github.com/michailr1/Model-Manager-LMM-")]
    [InlineData(" https://github.com/owner/repo.git/ ", "https://github.com/owner/repo")]
    public void RepositoryIsNormalized(string value, string expected) => Assert.Equal(expected, UpdateSettings.NormalizeRepository(value));

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://github.com.evil.test/owner/repo")]
    [InlineData("https://example.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/releases")]
    [InlineData("https://github.com/owner/repo?token=secret")]
    [InlineData("https://token@github.com/owner/repo")]
    [InlineData("https://github.com:444/owner/repo")]
    [InlineData("https://github.com/owner/repo#fragment")]
    [InlineData("")]
    public void InvalidSourcesAreRejected(string value) => Assert.Throws<ArgumentException>(() => UpdateSettings.NormalizeRepository(value));

    [Fact]
    public void PreferencesPersistAndOverridePackagedDefault()
    {
        var store = new UpdateSettingsStore(Path.Combine(root, "settings", "updates.json"));
        Assert.True(store.Load("https://github.com/owner/default").Automatic);
        store.Save(new("https://github.com/owner/chosen", false));
        Assert.Equal(new UpdateSettings("https://github.com/owner/chosen", false), store.Load("https://github.com/owner/default"));
        Assert.False(File.Exists(Path.Combine(root, "settings", "updates.json.tmp")));
    }

    [Fact]
    public void InvalidPreferencesDoNotReplaceSavedSettings()
    {
        var store = new UpdateSettingsStore(Path.Combine(root, "updates.json"));
        store.Save(new("https://github.com/owner/repo", false));
        Assert.Throws<ArgumentException>(() => store.Save(new("https://evil.test/owner/repo")));
        Assert.False(store.Load("").Automatic);
    }

    [Fact]
    public void CorruptSettingsAreReportedRatherThanSilentlyEnablingUpdates()
    {
        var path = Path.Combine(root, "updates.json");
        File.WriteAllText(path, "broken json");
        Assert.Throws<JsonException>(() => new UpdateSettingsStore(path).Load("https://github.com/owner/repo"));
    }

    private ApplicationUpdater Feed(string version = "0.7.0", bool corrupt = false, bool missing = false)
    {
        var feed = Directory.CreateDirectory(Path.Combine(root, "feed"));
        var packages = Directory.CreateDirectory(Path.Combine(root, "packages"));
        byte[] bytes = [1, 3, 5, 7, 9];
        var filename = $"LocalModelManager-{version}-full.nupkg";
        if (!missing) File.WriteAllBytes(Path.Combine(feed.FullName, filename), bytes);
        var asset = new VelopackAsset
        {
            PackageId = "LocalModelManager", Version = Velopack.SemanticVersion.Parse(version),
            Type = VelopackAssetType.Full, FileName = filename,
            Size = bytes.Length, SHA1 = corrupt ? new string('0', 40) : Convert.ToHexString(SHA1.HashData(bytes)),
            SHA256 = corrupt ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes))
        };
        File.WriteAllText(Path.Combine(feed.FullName, "releases.win-x64.json"),
            JsonSerializer.Serialize(new { Assets = new[] { new { asset.PackageId, Version = version, Type = "Full", asset.FileName, asset.Size, asset.SHA1, asset.SHA256 } } }));
        var locator = new TestVelopackLocator("LocalModelManager", "0.6.0", packages.FullName);
        return new ApplicationUpdater(new UpdateManager(new SimpleFileSource(feed),
            new UpdateOptions { ExplicitChannel = "win-x64" }, locator));
    }

    [Fact]
    public async Task DownloadsAndVerifiesAnUpdateBeforeAllowingApply()
    {
        var updater = Feed();
        Assert.Null(updater.ReadyVersion);
        Assert.Throws<InvalidOperationException>(() => updater.ApplyAndRestart());
        Assert.Equal("0.7.0", await updater.CheckAsync());
        await updater.DownloadAsync(_ => { }, CancellationToken.None);
        Assert.Equal("0.7.0", updater.ReadyVersion);
        Assert.True(File.Exists(Path.Combine(root, "packages", "LocalModelManager-0.7.0-full.nupkg")));
    }

    [Theory]
    [InlineData("0.6.0")]
    [InlineData("0.5.0")]
    public async Task DoesNotDowngradeOrUpdateToSameVersion(string version)
    {
        Assert.Null(await Feed(version).CheckAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InvalidDownloadCannotBeApplied(bool corrupt, bool missing)
    {
        var updater = Feed(corrupt: corrupt, missing: missing);
        await updater.CheckAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => updater.DownloadAsync(_ => { }, CancellationToken.None));
        Assert.Null(updater.ReadyVersion);
        Assert.Throws<InvalidOperationException>(() => updater.ApplyAndRestart());
    }

    [Fact]
    public async Task DownloadRequiresAnUpdateCheck()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Feed().DownloadAsync(_ => { }, CancellationToken.None));
    }
}
