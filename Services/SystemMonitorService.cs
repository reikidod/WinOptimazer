using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace WinOptimizer.Services;

public sealed record SystemSnapshot(
    string OsDescription,
    int LogicalProcessors,
    double CpuPercent,
    uint MemoryLoadPercent,
    ulong TotalMemoryBytes,
    ulong AvailableMemoryBytes,
    string SystemDrive,
    long DriveTotalBytes,
    long DriveFreeBytes,
    bool NetworkAvailable)
{
    /// <summary>PerformanceCounter может быть недоступен (например, отключены системные счётчики).</summary>
    public IReadOnlyList<DriveSnapshot> AdditionalDrives { get; init; } = Array.Empty<DriveSnapshot>();
    public bool CpuAvailable { get; init; } = true;
    public string CpuSummary => CpuAvailable ? $"{CpuPercent:0}%" : "—";
    public string CpuLoadSubtitle => CpuAvailable ? $"{LogicalProcessors} логических · загрузка" : "Показатель недоступен";
    public double CpuProgressValue => CpuAvailable ? CpuPercent : 0;
    public bool CpuProgressIsIndeterminate => !CpuAvailable;
    public string CpuProgressDescription => CpuAvailable ? "Загрузка процессора сейчас" : "Индикатор недоступен";
    public string MemoryUsedText => FormatBytes(TotalMemoryBytes - AvailableMemoryBytes);
    public string MemoryTotalText => FormatBytes(TotalMemoryBytes);
    public string DiskFreeText => FormatBytes((ulong)Math.Max(0, DriveFreeBytes));
    public string DiskTotalText => FormatBytes((ulong)Math.Max(0, DriveTotalBytes));
    public string MemorySummary => $"{MemoryUsedText} / {MemoryTotalText}";
    public string MemoryLoadSummary => $"{MemoryLoadPercent}% занято";
    public string MemoryLoadValue => $"{MemoryLoadPercent}%";
    public string MemoryUsageSubtitle => $"{MemoryUsedText} из {MemoryTotalText}";
    public string DiskSummary => $"{DiskFreeText} свободно из {DiskTotalText}";
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

public sealed record DriveSnapshot(
    string Name,
    string Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes)
{
    public DriveType DriveType { get; init; } = DriveType.Fixed;
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Name : $"{Name} · {Label}";
    public string FreeText => FormatBytes((ulong)Math.Max(0, FreeBytes));
    public string UsedText => FormatBytes((ulong)Math.Max(0, TotalBytes - FreeBytes));
    public string TotalText => FormatBytes((ulong)Math.Max(0, TotalBytes));
    public string DriveTypeText => DriveType switch
    {
        DriveType.Fixed => "Внутренний диск",
        DriveType.Removable => "Съёмный накопитель",
        DriveType.Network => "Сетевой диск",
        DriveType.CDRom => "Оптический привод",
        _ => "Тип накопителя не определён",
    };
    public string FileSystemText => string.IsNullOrWhiteSpace(FileSystem) ? "Файловая система не определена" : FileSystem;
    public string StorageTypeText => $"{DriveTypeText} · {FileSystemText}";
    public string UsageSummary => $"{UsedText} занято · {FreeText} свободно из {TotalText}";
    public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Clamp((TotalBytes - FreeBytes) * 100d / TotalBytes, 0, 100);

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// Снимок ресурсов Windows. CPU — через PerformanceCounter; память/диск — Win32 / DriveInfo.
/// Collect вызывается по запросу Dashboard (и по таймеру на главной).
/// </summary>
public static class SystemMonitorService
{
    private static PerformanceCounter? _cpu;
    private static readonly object CpuLock = new();
    private static bool _cpuInitTried;

    public static SystemSnapshot Collect()
    {
        EnsureCpuCounter();
        double cpuPct = 0;
        var cpuAvailable = false;
        try
        {
            lock (CpuLock)
            {
                if (_cpu is not null)
                {
                    cpuPct = _cpu.NextValue();
                    if (cpuPct < 0.1)
                    {
                        System.Threading.Thread.Sleep(120);
                        cpuPct = _cpu.NextValue();
                    }
                    cpuAvailable = true;
                }
            }
        }
        catch
        {
            // Не выдаём недоступный счётчик за реальную загрузку CPU 0%.
            cpuPct = 0;
            cpuAvailable = false;
        }

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
            Math.Clamp(cpuPct, 0, 100),
            memory.MemoryLoad,
            memory.TotalPhysical,
            memory.AvailablePhysical,
            systemRoot,
            total,
            free,
            NetworkInterface.GetIsNetworkAvailable())
        {
            CpuAvailable = cpuAvailable,
            AdditionalDrives = CollectAdditionalDrives(systemRoot),
        };
    }

    /// <summary>Возвращает готовые фиксированные и съёмные диски, кроме системного.</summary>
    public static IReadOnlyList<DriveSnapshot> CollectAdditionalDrives(string systemRoot)
    {
        var systemDriveRoot = Path.GetPathRoot(systemRoot);
        var result = new List<DriveSnapshot>();

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return result;
        }

        foreach (var drive in drives)
        {
            if (string.Equals(Path.GetPathRoot(drive.Name), systemDriveRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;

            try
            {
                if (!drive.IsReady) continue;
                var total = drive.TotalSize;
                if (total <= 0) continue;

                string label;
                string fileSystem;
                try { label = drive.VolumeLabel; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { label = string.Empty; }
                try { fileSystem = drive.DriveFormat; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { fileSystem = string.Empty; }

                result.Add(CreateDriveSnapshot(drive.Name, label, fileSystem, total, drive.AvailableFreeSpace, drive.DriveType));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or Win32Exception or ArgumentException)
            {
                // Один недоступный том не должен скрывать остальные диски.
            }
        }

        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Нормализует показатели тома; вынесено отдельно для детерминированных тестов.</summary>
    public static DriveSnapshot CreateDriveSnapshot(string name, string? label, string? fileSystem, long totalBytes, long freeBytes, DriveType driveType = DriveType.Fixed)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Имя диска обязательно.", nameof(name));
        if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));

        return new DriveSnapshot(
            name.Trim(),
            label?.Trim() ?? string.Empty,
            fileSystem?.Trim() ?? string.Empty,
            totalBytes,
            Math.Clamp(freeBytes, 0, totalBytes))
        {
            DriveType = driveType,
        };
    }

    private static void EnsureCpuCounter()
    {
        // Dashboard can be refreshed manually while its timer is running.
        // Guard initialization with the same lock used for NextValue().
        lock (CpuLock)
        {
            if (_cpuInitTried) return;
            _cpuInitTried = true;
            try
            {
                _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
                _ = _cpu.NextValue();
            }
            catch
            {
                _cpu?.Dispose();
                _cpu = null;
            }
        }
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
