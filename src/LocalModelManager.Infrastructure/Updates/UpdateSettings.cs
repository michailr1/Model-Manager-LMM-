using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalModelManager.Infrastructure.Updates;

public sealed record UpdateSettings(string RepositoryUrl = "", bool Automatic = true)
{
    public static string NormalizeRepository(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Укажите https://github.com/владелец/репозиторий (публичный репозиторий).");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(p => !Regex.IsMatch(p, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*\z")))
            throw new ArgumentException("Нужна ссылка на репозиторий, без /releases и других разделов.");
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (repo.Length == 0) throw new ArgumentException("Имя репозитория пустое.");
        return $"https://github.com/{parts[0]}/{repo}";
    }
}

public sealed class UpdateSettingsStore(string path)
{
    // Outside the installation directory so updates cannot replace preferences.
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalModelManager.Settings", "updates.json");

    public UpdateSettings Load(string defaultRepository)
    {
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path))
              ?? throw new InvalidDataException("Файл настроек обновления пустой.")
            : new UpdateSettings(defaultRepository);
        return settings with { RepositoryUrl = string.IsNullOrWhiteSpace(settings.RepositoryUrl) ? "" : UpdateSettings.NormalizeRepository(settings.RepositoryUrl) };
    }

    public void Save(UpdateSettings settings)
    {
        var normalized = settings with { RepositoryUrl = UpdateSettings.NormalizeRepository(settings.RepositoryUrl) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
