using LocalModelManager.Core;

namespace LocalModelManager.Infrastructure.LMStudio;

public static class LmsImport
{
    public static string[] Arguments(ImportRequest request, bool dryRun, bool copyApproved = false)
    {
        request.Validate();
        if (!dryRun && request.Mode == ImportMode.Copy && !copyApproved)
            throw new InvalidOperationException("Копирование требует явного подтверждения пользователя.");
        var mode = request.Mode switch
        {
            ImportMode.SymbolicLink => "--symbolic-link",
            ImportMode.Copy => "--copy",
            _ => throw new ArgumentException("Неизвестный режим импорта.")
        };
        var arguments = new List<string> { "import", request.File.FullPath, "--user-repo", request.Repository, mode, "--yes" };
        if (dryRun) arguments.Add("--dry-run");
        return arguments.ToArray();
    }
}
