using System.Text.RegularExpressions;

namespace LocalModelManager.Core;

public enum ImportMode { SymbolicLink, Copy }

public sealed record ImportRequest(ModelFile File, string Repository, ImportMode Mode)
{
    public static string SuggestRepository(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        name = Regex.Replace(name, @"[^a-z0-9._-]+", "-").Trim('-', '.', '_');
        if (name.Length > 100) name = name[..100].TrimEnd('.');
        if (name.Length == 0) name = "model";
        if (Regex.IsMatch(name, @"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)"))
            name = "model-" + name;
        if (name.Length > 100) name = name[..100].TrimEnd('.');
        return "local/" + name;
    }
    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("Неизвестный режим импорта.");
        var parts = Repository.Split('/');
        if (parts.Length != 2 || parts.Any(part =>
                !Regex.IsMatch(part, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$") || part.EndsWith('.') ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase)))
            throw new ArgumentException("Укажите publisher/repo: две части из латинских букв, цифр, '.', '_' или '-'. Например local/qwen3-32b.");
        var current = ModelFile.Read(File.FullPath);
        if (current.SizeBytes != File.SizeBytes)
            throw new IOException("Размер исходного файла изменился. Выберите файл заново.");
        using var stream = System.IO.File.OpenRead(current.FullPath);
        Span<byte> magic = stackalloc byte[4];
        if (stream.Read(magic) != 4 || !magic.SequenceEqual("GGUF"u8))
            throw new InvalidDataException("Файл не содержит сигнатуру GGUF.");
    }
}
