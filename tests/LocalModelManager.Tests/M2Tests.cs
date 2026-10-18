using LocalModelManager.Core;
using LocalModelManager.Infrastructure.LMStudio;
using Xunit;

namespace LocalModelManager.Tests;

public sealed class M2Tests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "LMM tests " + Guid.NewGuid());
    private readonly ModelFile file;
    private const string Help = "--user-repo --symbolic-link --copy --dry-run --yes";

    public M2Tests()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "модель Q5_K_M.gguf");
        File.WriteAllBytes(path, [71, 71, 85, 70, 3, 0, 0, 0]);
        file = ModelFile.Read(path);
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
    private ImportRequest Request(ImportMode mode = ImportMode.SymbolicLink) => new(file, "local/qwen3.8-27b", mode);

    [Theory]
    [InlineData("Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf", "local/qwen3.8-27b-abliterated-q5_k_m")]
    [InlineData("  My Model (Q5).GGUF", "local/my-model-q5")]
    [InlineData("модель.gguf", "local/model")]
    [InlineData("_модель Q5_K_M.gguf", "local/q5_k_m")]
    [InlineData("CON.gguf", "local/model-con")]
    [InlineData("COM1.test.gguf", "local/model-com1.test")]
    public void SuggestedRepositoryIsValid(string fileName, string expected)
    {
        var repository = ImportRequest.SuggestRepository(fileName);
        Assert.Equal(expected, repository);
        new ImportRequest(file, repository, ImportMode.SymbolicLink).Validate();
    }

    [Fact]
    public void LongSuggestedRepositoryRemainsValid()
    {
        var repository = ImportRequest.SuggestRepository("CON." + new string('a', 110) + ".gguf");
        Assert.Equal(100, repository.Split('/')[1].Length);
        new ImportRequest(file, repository, ImportMode.SymbolicLink).Validate();
    }
    [Fact]
    public void SymbolicLinkArgumentsNeverMoveOrCopy()
    {
        var arguments = LmsImport.Arguments(Request(), dryRun: false);
        Assert.Equal(new[] { "import", file.FullPath, "--user-repo", "local/qwen3.8-27b", "--symbolic-link", "--yes" }, arguments);
        Assert.Equal(file.SizeBytes, new FileInfo(file.FullPath).Length);
    }

    [Fact]
    public void CopyNeedsExplicitConsent()
    {
        Assert.Throws<InvalidOperationException>(() => LmsImport.Arguments(Request(ImportMode.Copy), dryRun: false));
        Assert.Contains("--copy", LmsImport.Arguments(Request(ImportMode.Copy), dryRun: false, copyApproved: true));
    }

    [Theory]
    [InlineData(ImportMode.Copy)]
    [InlineData(ImportMode.SymbolicLink)]
    public void PreviewAlwaysIncludesDryRun(ImportMode mode)
    {
        var arguments = LmsImport.Arguments(Request(mode), dryRun: true);
        Assert.Contains("--dry-run", arguments);
        Assert.Single(arguments, a => a is "--symbolic-link" or "--copy");
    }

    [Theory]
    [InlineData("")]
    [InlineData("../repo")]
    [InlineData("local/..")]
    [InlineData("local/repo/other")]
    [InlineData("C:/repo")]
    [InlineData("local\\repo")]
    [InlineData("local/repo.")]
    [InlineData("local/NUL")]
    [InlineData("local/COM1.gguf")]
    [InlineData("local/repo\n--copy")]
    public void InvalidNamespacesRejectedBeforeExecution(string repository) =>
        Assert.Throws<ArgumentException>(() => LmsImport.Arguments(new(file, repository, ImportMode.SymbolicLink), false));

    [Fact]
    public void InvalidModeCannotFallBackToMove() =>
        Assert.Throws<ArgumentException>(() => LmsImport.Arguments(Request((ImportMode)42), false));

    [Fact]
    public void InvalidSignatureRejected()
    {
        File.WriteAllBytes(file.FullPath, new byte[8]);
        Assert.Throws<InvalidDataException>(() => Request().Validate());
    }

    [Fact]
    public void ChangedSizeRejected()
    {
        File.AppendAllText(file.FullPath, "changed");
        Assert.Throws<IOException>(() => Request().Validate());
    }

    [Fact]
    public void RemovedSourceRejected()
    {
        File.Delete(file.FullPath);
        Assert.Throws<FileNotFoundException>(() => Request().Validate());
    }

    [Fact]
    public async Task UnsupportedCliDoesNotAttemptImport()
    {
        var calls = new List<string[]>();
        var provider = new LmsProvider("lms.exe", (_, args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new ProcessResult(0, "--copy --user-repo", ""));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PreviewImportAsync(Request(), CancellationToken.None));
        Assert.Single(calls);
        Assert.Equal(new[] { "import", "--help" }, calls[0]);
    }

    [Fact]
    public async Task PreviewChecksHelpThenRunsOnlyDryRun()
    {
        var calls = new List<string[]>();
        var provider = new LmsProvider("lms.exe", (_, args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(calls.Count == 1 ? new ProcessResult(0, "", Help) : new ProcessResult(0, "", "Would create a symbolic link"));
        });
        var result = await provider.PreviewImportAsync(Request(), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, calls.Count);
        Assert.Contains("--dry-run", calls[1]);
        Assert.Contains("--symbolic-link", calls[1]);
    }

    [Fact]
    public async Task LinkFailureNeverRetriesAsCopy()
    {
        var calls = new List<string[]>();
        var provider = new LmsProvider("lms.exe", (_, args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new ProcessResult(1, "", "EPERM: operation not permitted, symlink"));
        });
        var result = await provider.ImportAsync(Request(), false, CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("EPERM", result.StandardError);
        Assert.Single(calls);
        Assert.DoesNotContain("--copy", calls[0]);
        Assert.True(File.Exists(file.FullPath));
    }

    [Fact]
    public async Task CopyWithoutConsentNeverStartsProcess()
    {
        var called = false;
        var provider = new LmsProvider("lms.exe", (_, _, _) =>
        {
            called = true;
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.ImportAsync(Request(ImportMode.Copy), false, CancellationToken.None));
        Assert.False(called);
    }

    [Fact]
    public async Task ImportCancellationDoesNotRetry()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var calls = 0;
        var provider = new LmsProvider("lms.exe", (_, _, token) =>
        {
            calls++;
            return Task.FromCanceled<ProcessResult>(token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ImportAsync(Request(), false, source.Token));
        Assert.Equal(1, calls);
    }
}
