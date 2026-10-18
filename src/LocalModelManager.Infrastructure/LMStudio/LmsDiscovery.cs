namespace LocalModelManager.Infrastructure.LMStudio;

public static class LmsDiscovery
{
    public static string? Find(string? configuredPath = null) => Find(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), configuredPath, File.Exists);

    public static string? Find(string? path, string userProfile, string? configuredPath, Func<string, bool> exists)
    {
        foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), "lms.exe");
            if (exists(candidate)) return candidate;
        }
        var profileCandidate = Path.Combine(userProfile, ".lmstudio", "bin", "lms.exe");
        if (exists(profileCandidate)) return profileCandidate;
        if (!string.IsNullOrWhiteSpace(configuredPath) && exists(configuredPath)) return configuredPath;
        return null;
    }
}
