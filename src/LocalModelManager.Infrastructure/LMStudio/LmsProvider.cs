using LocalModelManager.Core;
using LocalModelManager.Infrastructure.Processes;

namespace LocalModelManager.Infrastructure.LMStudio;

public sealed class LmsProvider : IInferenceProvider
{
    private readonly string executable;
    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run;

    public LmsProvider(string executable, ProcessRunner runner) : this(executable, runner.RunAsync) { }

    public LmsProvider(string executable, Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run)
    {
        this.executable = executable;
        this.run = run;
    }

    public Task<ProcessResult> ListModelsAsync(CancellationToken cancellationToken) =>
        run(executable, ["ls"], cancellationToken);
    public Task<ProcessResult> GetServerStatusAsync(CancellationToken cancellationToken) =>
        run(executable, ["server", "status"], cancellationToken);

    public async Task<ProcessResult> PreviewImportAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        var arguments = LmsImport.Arguments(request, dryRun: true);
        var help = await run(executable, ["import", "--help"], cancellationToken);
        var text = LmsOutput.CombinedText(help);
        var mode = request.Mode == ImportMode.Copy ? "--copy" : "--symbolic-link";
        if (help.ExitCode != 0 || new[] { mode, "--user-repo", "--dry-run", "--yes" }.Any(flag => !text.Contains(flag, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Эта версия LMS не подтверждает поддержку безопасного импорта. Обновите LM Studio. exit={help.ExitCode}\n{text}");
        return await run(executable, arguments, cancellationToken);
    }

    public Task<ProcessResult> ImportAsync(ImportRequest request, bool copyApproved, CancellationToken cancellationToken) =>
        run(executable, LmsImport.Arguments(request, dryRun: false, copyApproved), cancellationToken);
}
