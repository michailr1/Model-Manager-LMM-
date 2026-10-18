using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalModelManager.Infrastructure.Hardware;

public sealed record SystemMemorySnapshot(ulong TotalBytes, ulong AvailableBytes);

public static class SystemMemoryInfo
{
    public static SystemMemorySnapshot Read()
    {
        var status = new MemoryStatus();
        if (!GlobalMemoryStatusEx(status)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось прочитать свободную RAM Windows.");
        return new(status.TotalPhysical, status.AvailablePhysical);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}