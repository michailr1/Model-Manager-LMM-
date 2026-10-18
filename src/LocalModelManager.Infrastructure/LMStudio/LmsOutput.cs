using System.Text.RegularExpressions;
using LocalModelManager.Core;

namespace LocalModelManager.Infrastructure.LMStudio;

public enum ServerState { Unknown, Running, Stopped }

public static class LmsOutput
{
    public static string CombinedText(ProcessResult result) => string.Join(Environment.NewLine,
        new[] { Clean(result.StandardOutput), Clean(result.StandardError) }.Where(text => text.Length > 0));

    // Preserve CLI tables verbatim: their column layout varies between LMS releases.
    public static string Clean(string text) => Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Trim();

    public static ServerState ParseServerState(string text)
    {
        var clean = Clean(text);
        if (Regex.IsMatch(clean, @"\b(not running|stopped)\b", RegexOptions.IgnoreCase)) return ServerState.Stopped;
        if (Regex.IsMatch(clean, @"\b(running|listening)\b", RegexOptions.IgnoreCase)) return ServerState.Running;
        return ServerState.Unknown;
    }
}
