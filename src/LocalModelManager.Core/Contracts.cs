namespace LocalModelManager.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed record GpuInfo(string Name, long TotalMemoryMiB);
public sealed record ModelFile(string FullPath, string FileName, long SizeBytes)
{
    public static ModelFile Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите один файл .gguf.");
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Файл не найден.", path);
        return new(file.FullName, file.Name, file.Length);
    }
}

public interface IInferenceProvider
{
    Task<ProcessResult> ListModelsAsync(CancellationToken cancellationToken);
    Task<ProcessResult> GetServerStatusAsync(CancellationToken cancellationToken);
    Task<ProcessResult> PreviewImportAsync(ImportRequest request, CancellationToken cancellationToken);
    Task<ProcessResult> ImportAsync(ImportRequest request, bool copyApproved, CancellationToken cancellationToken);
}

public interface IHardwareInfoService
{
    Task<IReadOnlyList<GpuInfo>> GetGpusAsync(CancellationToken cancellationToken);
}
