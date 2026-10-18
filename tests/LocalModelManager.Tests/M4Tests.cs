using LocalModelManager.Core;
using LocalModelManager.Infrastructure.Hardware;
using LocalModelManager.Infrastructure.LMStudio;
using System.Globalization;
using Xunit;

namespace LocalModelManager.Tests;

public sealed class M4Tests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "LMM M4 " + Guid.NewGuid());
    private readonly ModelFile file;
    private readonly MemoryEstimate estimate = new(70, 20.60, 28.52);
    private const string Key = "model-key";
    private const string Catalog = "[{\"modelKey\":\"model-key\",\"path\":\"local/model/model.gguf\",\"type\":\"llm\",\"format\":\"gguf\",\"deviceIdentifier\":null,\"maxContextLength\":262144}]";
    private readonly List<string[]> calls = [];
    private string? identifier;
    private string beforeLoaded = "[]";
    private int loadExit;
    private int estimateExit;
    private bool loaded;
    private bool changedCatalog;
    private Func<string, string>? finalJson;
    private Func<CancellationToken, Task<ProcessResult>>? loadAction;
    private Func<CancellationToken, Task<GpuMemorySnapshot>> gpuReader = _ => Task.FromResult(new GpuMemorySnapshot("RTX", 24564, 22800));
    private Func<SystemMemorySnapshot> ramReader = () => new(64UL * 1073741824, 45UL * 1073741824);
    public M4Tests()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "model.gguf");
        File.WriteAllBytes(path, "GGUF"u8.ToArray());
        file = ModelFile.Read(path);
    }
    public void Dispose() => Directory.Delete(directory, true);
    private MemoryAdvice Advice() => new(Key, 21, 1.2, [estimate], estimate);
    private Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        var args = arguments.ToArray(); calls.Add(args);
        if (args.Contains("--help")) return Task.FromResult(new ProcessResult(0, "--identifier --context-length --gpu --parallel --estimate-only --yes", ""));
        if (args[0] == "ls") return Task.FromResult(new ProcessResult(0, changedCatalog ? Catalog.Replace(Key, "another-key") : Catalog, ""));
        if (args[0] == "ps")
        {
            var json = loaded ? (finalJson?.Invoke(identifier!) ?? LoadedJson(identifier!)) : beforeLoaded;
            return Task.FromResult(new ProcessResult(0, json, ""));
        }
        Assert.Equal("load", args[0]);
        if (args.Contains("--estimate-only")) return Task.FromResult(new ProcessResult(estimateExit, "", "Model: model-key\nContext Length: 65,536\nGPU Offload: 70%\nEstimated GPU Memory: 20.60 GiB\nEstimated Total Memory: 28.52 GiB"));
        loaded = true;
        identifier = args[Array.IndexOf(args, "--identifier") + 1];
        return loadAction?.Invoke(token) ?? Task.FromResult(new ProcessResult(loadExit, "", loadExit == 0 ? "Model loaded" : "Out of memory"));
    }
    private ModelLoader Loader() => new("lms", Run, ct => gpuReader(ct), () => ramReader());
    private Task<ModelLoadResult> Load(Func<LoadPlan, bool> confirm, CancellationToken ct = default) =>
        Loader().LoadAsync(file, "local/model", Advice(), confirm, (_, _) => {}, ct);
    private static string LoadedJson(string id, int context = 65536) =>
        $"[{{\"identifier\":\"{id}\",\"modelKey\":\"model-key\",\"path\":\"local/model/model.gguf\",\"deviceIdentifier\":null,\"contextLength\":{context}}}]";
    private IEnumerable<string[]> ActualLoads => calls.Where(a => a[0] == "load" && !a.Contains("--help") && !a.Contains("--estimate-only"));

    [Fact]
    public async Task RefusalNeverStartsRealLoad()
    {
        var result = await Load(plan => { Assert.Equal(70, plan.Estimate.OffloadPercent); Assert.True(plan.FreeRamGiB > 28.52); return false; });
        Assert.Equal(ModelLoadState.Declined, result.State);
        Assert.Empty(ActualLoads);
        Assert.False(loaded);
    }
    [Fact]
    public async Task LoadsOnlyExactLocalPathAtApprovedContextAndChecksInstance()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var result = await Load(_ => true);
            Assert.Equal(ModelLoadState.Verified, result.State);
            var args = Assert.Single(ActualLoads);
            Assert.Equal("local/model/model.gguf", args[1]);
            Assert.Contains("--exact", args); Assert.Contains("--local", args);
            Assert.Equal("65536", args[Array.IndexOf(args, "--context-length") + 1]);
            Assert.Equal("0.70", args[Array.IndexOf(args, "--gpu") + 1]);
            Assert.Equal("1", args[Array.IndexOf(args, "--parallel") + 1]);
            Assert.StartsWith("lmm-", result.Plan.Identifier);
            Assert.Equal("ps", calls[^1][0]);
            Assert.DoesNotContain(calls, a => a[0] is "unload" or "server");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    [Fact]
    public async Task ExistingModelIsNeverUnloadedOrDuplicated()
    {
        beforeLoaded = "[{\"identifier\":\"existing\"}]";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Load(_ => throw new Exception("Must not prompt")));
        Assert.Empty(ActualLoads);
        Assert.DoesNotContain(calls, a => a[0] == "unload");
    }
    [Fact]
    public async Task FreshEstimateFailureNeverPromptsOrLoads()
    {
        estimateExit = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Load(_ => throw new Exception("Must not prompt")));
        Assert.Empty(ActualLoads);
    }
    [Fact]
    public async Task InsufficientRamNeverPromptsOrLoads()
    {
        ramReader = () => new(64UL * 1073741824, 20UL * 1073741824);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Load(_ => throw new Exception("Must not prompt")));
        Assert.Empty(ActualLoads);
    }
    [Fact]
    public async Task ResourcesAreRecheckedAfterConfirmation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Load(_ =>
        {
            gpuReader = _ => Task.FromResult(new GpuMemorySnapshot("RTX", 24564, 15000)); return true;
        }));
        Assert.Empty(ActualLoads);
    }
    [Fact]
    public async Task CatalogChangeAfterConfirmationStopsLoad()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Load(_ => { changedCatalog = true; return true; }));
        Assert.Empty(ActualLoads);
    }
    [Fact]
    public async Task SourceChangeAfterConfirmationStopsLoad()
    {
        await Assert.ThrowsAsync<IOException>(() => Load(_ => { File.AppendAllText(file.FullPath, "changed"); return true; }));
        Assert.Empty(ActualLoads);
    }
    [Fact]
    public async Task CancelAtConfirmationDoesNotStartLoad()
    {
        using var source = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Load(_ => { source.Cancel(); return true; }, source.Token));
        Assert.Empty(ActualLoads);
    }
    [Fact]
    public async Task CliSuccessWithClampedContextIsUnconfirmed()
    {
        finalJson = id => LoadedJson(id, 32768);
        var result = await Load(_ => true);
        Assert.Equal(ModelLoadState.Unconfirmed, result.State);
        Assert.Single(ActualLoads);
    }
    [Fact]
    public async Task WrongInstanceCannotReportSuccess()
    {
        finalJson = _ => LoadedJson("different-instance");
        Assert.Equal(ModelLoadState.Unconfirmed, (await Load(_ => true)).State);
    }
    [Fact]
    public async Task CliFailureNeverRetriesOrSilentlyReducesContext()
    {
        loadExit = 7; finalJson = _ => "[]";
        var result = await Load(_ => true);
        Assert.Equal(ModelLoadState.Unconfirmed, result.State);
        Assert.Contains("exit=7", result.Message);
        Assert.Single(ActualLoads);
    }
    [Fact]
    public async Task BackendCanRemainLoadedAfterCancellationAndIsInspected()
    {
        using var source = new CancellationTokenSource();
        loadAction = token => { source.Cancel(); return Task.FromCanceled<ProcessResult>(token); };
        var result = await Load(_ => true, source.Token);
        Assert.Equal(ModelLoadState.Unconfirmed, result.State);
        Assert.Contains("экземпляр уже обнаружен", result.Message);
        Assert.Equal("ps", calls[^1][0]);
        Assert.Single(ActualLoads);
        Assert.DoesNotContain(calls, a => a[0] == "unload");
    }
    [Fact]
    public void RejectsInvalidResourceSnapshots()
    {
        Assert.Throws<InvalidOperationException>(() => ModelLoader.ValidateResources(estimate, new("RTX", 24564, 25000), ramReader()));
        Assert.Throws<InvalidOperationException>(() => ModelLoader.ValidateResources(estimate with { GpuGiB = double.NaN }, new("RTX", 24564, 22800), ramReader()));
    }
}