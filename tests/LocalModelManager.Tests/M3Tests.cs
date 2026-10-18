using System.Globalization;
using LocalModelManager.Core;
using LocalModelManager.Infrastructure.Hardware;
using LocalModelManager.Infrastructure.LMStudio;
using Xunit;

namespace LocalModelManager.Tests;

public sealed class M3Tests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "LMM M3 " + Guid.NewGuid());
    private readonly ModelFile file;
    private const string Key = "qwen3.8-27b-abliterated";
    private const string Help = "--estimate-only --context-length --gpu --yes";
    private const string Catalog = "[{\"modelKey\":\"qwen3.8-27b-abliterated\",\"path\":\"local/model/model.gguf\",\"type\":\"llm\",\"format\":\"gguf\",\"deviceIdentifier\":null,\"maxContextLength\":262144}]";
    public M3Tests()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "model.gguf");
        File.WriteAllBytes(path, "GGUF"u8.ToArray());
        file = ModelFile.Read(path);
    }
    public void Dispose() => Directory.Delete(directory, true);
    private static ProcessResult Estimate(int percent, double gpu = 21.88, double total = 29) =>
        new(0, "", FormattableString.Invariant($"Model: {Key}\nContext Length: 65,536\nGPU Offload: {percent}%\nEstimated GPU Memory: {gpu:F2} GiB\nEstimated Total Memory: {total:F2} GiB\n"));

    [Fact]
    public void RejectsRemoteAndAmbiguousModelKeys()
    {
        Assert.Throws<InvalidOperationException>(() => MemoryAdvisor.ResolveModel(Catalog.Replace("\"deviceIdentifier\":null", "\"deviceIdentifier\":\"remote\""), "local/model", "model.gguf"));
        var second = Catalog.Trim('[', ']').Replace("local/model/model.gguf", "other/model/model.gguf");
        Assert.Throws<InvalidOperationException>(() => MemoryAdvisor.ResolveModel(Catalog.TrimEnd(']') + "," + second + "]", "local/model", "model.gguf"));
    }
    [Fact]
    public async Task CancellationDuringFittingResultCannotReturnRecommendation()
    {
        using var source = new CancellationTokenSource();
        var advisor = new MemoryAdvisor("lms", (_, arguments, _) =>
        {
            var args = arguments.ToArray();
            if (args.Contains("--help")) return Task.FromResult(new ProcessResult(0, Help, ""));
            if (args[0] == "ls") return Task.FromResult(new ProcessResult(0, Catalog, ""));
            source.Cancel(); return Task.FromResult(Estimate(100, 10, 20));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advisor.AdviseAsync(file, "local/model", new("RTX", 24564, 23000), (_, _) => {}, source.Token));
    }
    [Fact]
    public void ParsesRealEstimateOnStderrAndAnsi()
    {
        var result = Estimate(100, 28.52, 28.52);
        result = result with { StandardError = "\u001b[32m" + result.StandardError + "\u001b[0m" };
        Assert.Equal(new MemoryEstimate(100, 28.52, 28.52), MemoryAdvisor.ParseEstimate(result, Key, 100));
    }
    [Fact]
    public void ParsesBinaryMiBAndDecimalGB()
    {
        var result = Estimate(75) with { StandardError = Estimate(75).StandardError.Replace("21.88 GiB", "20480 MiB").Replace("29.00 GiB", "32 GB") };
        var estimate = MemoryAdvisor.ParseEstimate(result, Key, 75);
        Assert.Equal(20, estimate.GpuGiB);
        Assert.Equal(32e9 / 1073741824, estimate.TotalGiB, 8);
    }
    [Theory]
    [InlineData("Context Length: 65,536", "Context Length: 32,768")]
    [InlineData("GPU Offload: 75%", "GPU Offload: 100%")]
    [InlineData("Model: qwen3.8-27b-abliterated", "Model: another-model")]
    [InlineData("21.88 GiB", "N/A")]
    [InlineData("21.88 GiB", "0.00 GiB")]
    [InlineData("29.00 GiB", "1.00 GiB")]
    public void RejectsUnsafeOrUnreadableEstimate(string from, string to)
    {
        var result = Estimate(75);
        Assert.Throws<FormatException>(() => MemoryAdvisor.ParseEstimate(result with { StandardError = result.StandardError.Replace(from, to) }, Key, 75));
    }
    [Fact]
    public void RejectsConflictingStreams()
    {
        var result = Estimate(75);
        Assert.Throws<FormatException>(() => MemoryAdvisor.ParseEstimate(result with { StandardOutput = result.StandardError }, Key, 75));
    }
    [Fact]
    public void ResolvesActualKeyAndRejectsInsufficientContext()
    {
        Assert.Equal(Key, MemoryAdvisor.ResolveModel(Catalog, "local/model", "model.gguf"));
        Assert.Throws<InvalidOperationException>(() => MemoryAdvisor.ResolveModel(Catalog.Replace("262144", "32768"), "local/model", "model.gguf"));
        Assert.Throws<InvalidOperationException>(() => MemoryAdvisor.ResolveModel(Catalog, "local/other", "model.gguf"));
        Assert.Throws<InvalidOperationException>(() => MemoryAdvisor.ResolveModel(Catalog.Replace(",\"maxContextLength\":262144", ""), "local/model", "model.gguf"));
    }
    [Fact]
    public async Task IteratesSafelyWithReserveAndInvariantArguments()
    {
        var calls = new List<string[]>();
        var reports = 0;
        var provider = new MemoryAdvisor("lms.exe", (_, arguments, _) =>
        {
            var args = arguments.ToArray(); calls.Add(args);
            if (args.Contains("--help")) return Task.FromResult(new ProcessResult(0, Help, ""));
            if (args[0] == "ls") return Task.FromResult(new ProcessResult(0, Catalog, ""));
            Assert.Contains("--estimate-only", args);
            Assert.Contains("--yes", args);
            Assert.Equal("65536", args[Array.IndexOf(args, "--context-length") + 1]);
            Assert.Equal(Key, args[1]);
            var ratio = args[Array.IndexOf(args, "--gpu") + 1];
            var percent = (int)Math.Round(double.Parse(ratio, CultureInfo.InvariantCulture) * 100);
            return Task.FromResult(Estimate(percent, percent >= 95 ? 27.42 : 20));
        });
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var advice = await provider.AdviseAsync(file, "local/model", new("RTX", 24564, 23000), (_, _) => reports++, CancellationToken.None);
            Assert.Equal(90, advice.Recommendation!.OffloadPercent);
            Assert.Equal(new[] {100, 95, 90}, advice.Attempts.Select(a => a.OffloadPercent));
            Assert.True(advice.Recommendation.GpuGiB < advice.BudgetGiB);
            Assert.Equal(1229 / 1024.0, advice.ReserveGiB);
            Assert.Equal(5, reports);
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }
    [Theory]
    [InlineData("--gpu --context-length --yes", 0)]
    [InlineData(Help, 7)]
    public async Task MissingEstimateCapabilityOrFailureNeverLoads(string help, int estimateExit)
    {
        var calls = new List<string[]>();
        var advisor = new MemoryAdvisor("lms", (_, arguments, _) =>
        {
            var args = arguments.ToArray(); calls.Add(args);
            return Task.FromResult(args.Contains("--help") ? new ProcessResult(0, help, "") : args[0] == "ls" ?
                new ProcessResult(0, Catalog, "") : new ProcessResult(estimateExit, "", "estimation failed"));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => advisor.AdviseAsync(file, "local/model", new("RTX", 24564, 23000), (_, _) => {}, CancellationToken.None));
        Assert.All(calls.Where(a => a[0] == "load" && !a.Contains("--help")), a => Assert.Contains("--estimate-only", a));
    }
    [Fact]
    public async Task NoFitStopsAtZeroWithoutReducingContext()
    {
        var advisor = new MemoryAdvisor("lms", (_, arguments, _) =>
        {
            var args = arguments.ToArray();
            return Task.FromResult(args.Contains("--help") ? new ProcessResult(0, Help, "") : args[0] == "ls" ?
                new ProcessResult(0, Catalog, "") : Estimate((int)Math.Round(double.Parse(args[Array.IndexOf(args, "--gpu") + 1], CultureInfo.InvariantCulture) * 100), 30, 35));
        });
        var advice = await advisor.AdviseAsync(file, "local/model", new("RTX", 24564, 23000), (_, _) => {}, CancellationToken.None);
        Assert.Null(advice.Recommendation);
        Assert.Equal(21, advice.Attempts.Count);
        Assert.Equal(0, advice.Attempts[^1].OffloadPercent);
    }
    [Fact]
    public async Task CancellationStopsBeforeNextEstimate()
    {
        using var source = new CancellationTokenSource();
        var estimateCalls = 0;
        var advisor = new MemoryAdvisor("lms", (_, arguments, _) =>
        {
            var args = arguments.ToArray();
            if (args.Contains("--help")) return Task.FromResult(new ProcessResult(0, Help, ""));
            if (args[0] == "ls") return Task.FromResult(new ProcessResult(0, Catalog, ""));
            estimateCalls++; source.Cancel(); return Task.FromResult(Estimate(100, 30, 35));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advisor.AdviseAsync(file, "local/model", new("RTX", 24564, 23000), (_, _) => {}, source.Token));
        Assert.Equal(1, estimateCalls);
    }
    [Theory]
    [InlineData("GPU, 24564, 22851", true)]
    [InlineData("GPU, 24564, 0", true)]
    [InlineData("GPU, 24564, N/A", false)]
    [InlineData("GPU, 24564, 25000", false)]
    public void ReadsFreeMemoryConservatively(string csv, bool valid)
    {
        if (valid) Assert.Equal(24564, NvidiaHardwareInfoService.ParseSingleGpuMemory(csv).TotalMiB);
        else Assert.Throws<FormatException>(() => NvidiaHardwareInfoService.ParseSingleGpuMemory(csv));
    }
    [Fact]
    public void MultipleGpusAreNotSummed() => Assert.Throws<InvalidOperationException>(() =>
        NvidiaHardwareInfoService.ParseSingleGpuMemory("GPU 1, 24564, 22000\nGPU 2, 24564, 22000"));
}