using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;

namespace WinOptimizer.Services;

public sealed record SystemSnapshot(
    string OsDescription,
    int LogicalProcessors,
    uint MemoryLoadPercent,
    ulong TotalMemoryBytes,
    ulong AvailableMemoryBytes,
    string SystemDrive,
    long DriveTotalBytes,
    long DriveFreeBytes,
    bool NetworkAvailable)
{
    public string CpuSummary => $"{LogicalProcessors} логических процессоров";
    public string MemorySummary => $"{FormatBytes(TotalMemoryBytes - AvailableMemoryBytes)} / {FormatBytes(TotalMemoryBytes)}";
    public string MemoryLoadSummary => $"{MemoryLoadPercent}% занято";
    public string DiskSummary => $"{FormatBytes((ulong)Math.Max(0, DriveFreeBytes))} свободно из {FormatBytes((ulong)Math.Max(0, DriveTotalBytes))}";
    public double DiskFreePercent => DriveTotalBytes <= 0 ? 0 : Math.Clamp(DriveFreeBytes * 100d / DriveTotalBytes, 0, 100);
    public string NetworkSummary => NetworkAvailable ? "Подключение доступно" : "Нет активного сетевого подключения";

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// Быстрый снимок ресурсов Windows без постоянных WMI-опросов и фонового таймера.
/// Обновляется только по запросу пользователя или при открытии Dashboard.
/// </summary>
public static class SystemMonitorService
{
    public static SystemSnapshot Collect()
    {
        var memory = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(memory))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось получить состояние памяти Windows.");

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(systemRoot))
            systemRoot = "C:\\";

        var drive = new DriveInfo(systemRoot);
        var total = drive.IsReady ? drive.TotalSize : 0;
        var free = drive.IsReady ? drive.AvailableFreeSpace : 0;

        return new SystemSnapshot(
            RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            memory.MemoryLoad,
            memory.TotalPhysical,
            memory.AvailablePhysical,
            systemRoot,
            total,
            free,
            NetworkInterface.GetIsNetworkAvailable());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
}
