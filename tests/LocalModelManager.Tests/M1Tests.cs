using LocalModelManager.Core;
using LocalModelManager.Infrastructure.Hardware;
using LocalModelManager.Infrastructure.LMStudio;
using LocalModelManager.Infrastructure.Processes;
using Xunit;

namespace LocalModelManager.Tests;

public class M1Tests
{
    [Theory]
    [InlineData("", "The server is running on port 1234.", ServerState.Running)]
    [InlineData("The server is running on port 1234.", "", ServerState.Running)]
    [InlineData("", "The server is not running.", ServerState.Stopped)]
    [InlineData("Waking up LM Studio service...", "\u001b[32mThe server is running on port 1234.\u001b[0m", ServerState.Running)]
    public void ReadsServerStatusFromEitherStream(string stdout, string stderr, ServerState expected)
    {
        var text = LmsOutput.CombinedText(new ProcessResult(0, stdout, stderr));
        Assert.Contains("server", text);
        Assert.Equal(expected, LmsOutput.ParseServerState(text));
    }

    [Fact]
    public void CombinedOutputRetainsBothStreams()
    {
        var text = LmsOutput.CombinedText(new ProcessResult(0, "model table\r\n", "Waking up LM Studio service..."));
        Assert.Equal("model table" + Environment.NewLine + "Waking up LM Studio service...", text);
    }

    [Fact]
    public void DiscoveryPrefersPathThenProfileThenConfiguration()
    {
        var first = Path.Combine("C:\\tools one", "lms.exe");
        var second = Path.Combine("D:\\tools", "lms.exe");
        var profile = Path.Combine("C:\\Users\\admin", ".lmstudio", "bin", "lms.exe");
        var configured = "E:\\custom\\lms.exe";
        var available = new HashSet<string> { first, second, profile, configured };
        string? Find() => LmsDiscovery.Find("\"C:\\tools one\";D:\\tools", "C:\\Users\\admin", configured, available.Contains);
        Assert.Equal(first, Find());
        available.Remove(first); Assert.Equal(second, Find());
        available.Remove(second); Assert.Equal(profile, Find());
        available.Remove(profile); Assert.Equal(configured, Find());
        available.Clear(); Assert.Null(Find());
    }

    [Fact]
    public void DiscoveryHandlesMissingPathAndConfiguration() =>
        Assert.Null(LmsDiscovery.Find(null, "C:\\Users\\test", null, _ => false));

    [Theory]
    [InlineData("Server is running on port 1234.", ServerState.Running)]
    [InlineData("The server is not running.", ServerState.Stopped)]
    [InlineData("\u001b[32mServer is running\u001b[0m", ServerState.Running)]
    [InlineData("Server stopped", ServerState.Stopped)]
    [InlineData("Cannot connect to LM Studio", ServerState.Unknown)]
    [InlineData("", ServerState.Unknown)]
    public void ParsesRepresentativeStatusText(string text, ServerState expected) =>
        Assert.Equal(expected, LmsOutput.ParseServerState(text));

    [Fact]
    public void PreservesModelTableAndUnicodePaths()
    {
        const string table = "MODEL                         SIZE\nqwen/qwen3-32b Q4_K_M         19.8 GB\nE:\\модели\\model.gguf";
        Assert.Equal(table, LmsOutput.Clean("\u001b[1m" + table + "\u001b[0m\r\n"));
    }

    [Fact]
    public void ParsesLargeVramAndMultipleGpus()
    {
        var result = NvidiaHardwareInfoService.Parse("NVIDIA GeForce RTX 4090, 24564\r\nNVIDIA RTX A6000, 49140\n");
        Assert.Equal(2, result.Count);
        Assert.Equal(24564, result[0].TotalMemoryMiB);
        Assert.Equal("NVIDIA GeForce RTX 4090", result[0].Name);
    }

    [Theory]
    [InlineData("GPU, N/A")]
    [InlineData("invalid")]
    [InlineData("GPU, -1")]
    public void RejectsInvalidGpuMemory(string text) =>
        Assert.Throws<FormatException>(() => NvidiaHardwareInfoService.Parse(text));

    [Fact]
    public void ReadsRealFileMetadataWithoutChangingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".GGUF");
        try
        {
            File.WriteAllBytes(path, [71, 71, 85, 70, 3, 0, 0, 0]);
            var model = ModelFile.Read(path);
            Assert.Equal(Path.GetFullPath(path), model.FullPath);
            Assert.Equal(Path.GetFileName(path), model.FileName);
            Assert.Equal(8, model.SizeBytes);
            Assert.Equal(8, new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsWrongExtension() => Assert.Throws<ArgumentException>(() => ModelFile.Read("model.txt"));

    [Fact]
    public async Task CapturesOutputErrorAndExitCode()
    {
        var result = await new ProcessRunner().RunAsync("powershell.exe",
            ["-NoProfile", "-Command", "[Console]::Out.Write('out'); [Console]::Error.Write('err'); exit 7"], CancellationToken.None);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("out", result.StandardOutput);
        Assert.Equal("err", result.StandardError);
    }

    [Fact]
    public async Task CancelsRunningProcess()
    {
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            "powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], source.Token));
    }
}
