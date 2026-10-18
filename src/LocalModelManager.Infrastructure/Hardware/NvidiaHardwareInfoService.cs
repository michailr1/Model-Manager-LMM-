using System.Globalization;
using LocalModelManager.Core;
using LocalModelManager.Infrastructure.Processes;

namespace LocalModelManager.Infrastructure.Hardware;

public sealed class NvidiaHardwareInfoService(ProcessRunner runner) : IHardwareInfoService
{
    public async Task<IReadOnlyList<GpuInfo>> GetGpusAsync(CancellationToken cancellationToken)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(executable)) executable = "nvidia-smi.exe";
        var result = await runner.RunAsync(executable,
            ["--query-gpu=name,memory.total", "--format=csv,noheader,nounits"], cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"nvidia-smi exit={result.ExitCode}: {result.StandardError}");
        return Parse(result.StandardOutput);
    }

    public async Task<GpuMemorySnapshot> GetSingleGpuMemoryAsync(CancellationToken token)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(executable)) executable = "nvidia-smi.exe";
        var result = await runner.RunAsync(executable,
            ["--query-gpu=name,memory.total,memory.free", "--format=csv,noheader,nounits"], token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"nvidia-smi exit={result.ExitCode}: {result.StandardError}");
        return ParseSingleGpuMemory(result.StandardOutput);
    }

    public static GpuMemorySnapshot ParseSingleGpuMemory(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
            throw new InvalidOperationException("M3 требует ровно один NVIDIA GPU; автоматический выбор нескольких GPU пока не поддерживается.");
        var parts = lines[0].Split(',');
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) ||
            !long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) ||
            !long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var free) ||
            total <= 0 || free < 0 || free > total)
            throw new FormatException("Не удалось прочитать общий и свободный объём VRAM NVIDIA.");
        return new(parts[0].Trim(), total, free);
    }
    public static IReadOnlyList<GpuInfo> Parse(string output)
    {
        var gpus = new List<GpuInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.LastIndexOf(',');
            if (separator <= 0 || !long.TryParse(line[(separator + 1)..].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var memory) || memory <= 0)
                throw new FormatException($"Не удалось прочитать данные NVIDIA: {line}");
            gpus.Add(new(line[..separator].Trim(), memory));
        }
        if (gpus.Count == 0) throw new InvalidOperationException("NVIDIA GPU не обнаружен.");
        return gpus;
    }
}

public sealed record GpuMemorySnapshot(string Name, long TotalMiB, long FreeMiB);
