using System.ComponentModel;
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
    /// <summary>Первый системный замер CPU задаёт базу для расчёта.</summary>
    public IReadOnlyList<DriveSnapshot> AdditionalDrives { get; init; } = Array.Empty<DriveSnapshot>();
    public IReadOnlyList<PhysicalDiskActivitySnapshot> PhysicalDiskActivity { get; init; } = Array.Empty<PhysicalDiskActivitySnapshot>();
    public IReadOnlyList<GpuAdapterActivitySnapshot> GpuActivity { get; init; } = Array.Empty<GpuAdapterActivitySnapshot>();
    public IReadOnlyList<TemperatureMetricSnapshot> TemperatureMetrics { get; init; } = Array.Empty<TemperatureMetricSnapshot>();
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
    public bool CpuAvailable { get; init; } = true;
    public NetworkTrafficRate NetworkTraffic { get; init; } = new(null, null);
    public string NetworkTrafficSummary => NetworkTraffic.Summary;
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

    /// <summary>Представляет текущие системные показатели единым контрактом метрик.</summary>
    public IReadOnlyList<TelemetryMetricSnapshot> ToMetricSnapshots()
    {
        var metrics = new List<TelemetryMetricSnapshot>
        {
            new("CPU", CpuAvailable ? CpuPercent : null, "%", "GetSystemTimes", CapturedAt,
                CpuAvailable ? MetricAvailability.Available : MetricAvailability.Unavailable,
                CpuAvailable ? null : "Счётчик процессора недоступен"),
            new("Использование RAM", MemoryLoadPercent, "%", "GlobalMemoryStatusEx", CapturedAt, MetricAvailability.Available),
            new("Свободно на системном диске", Math.Max(0, DriveFreeBytes), "Б", "DriveInfo", CapturedAt,
                DriveTotalBytes > 0 ? MetricAvailability.Available : MetricAvailability.Unavailable,
                DriveTotalBytes > 0 ? null : "Системный том недоступен"),
        };

        // Сетевой трафик является частью общего контракта метрик Dashboard/Performance Center.
        // Нулевые или ещё не прогретые счётчики не подменяем нулём: сохраняем состояние «измерение».
        AddNetworkMetric(metrics, "Сеть · входящий трафик", NetworkTraffic.ReceivedBytesPerSecond);
        AddNetworkMetric(metrics, "Сеть · исходящий трафик", NetworkTraffic.SentBytesPerSecond);

        metrics.AddRange(GpuActivity.Select(item => item.ToMetricSnapshot()));
        metrics.AddRange(PhysicalDiskActivity.SelectMany(item => item.ToMetricSnapshots()));
        metrics.AddRange(TemperatureMetrics.Select(item => item.ToMetricSnapshot()));
        return metrics;
    }


    private void AddNetworkMetric(List<TelemetryMetricSnapshot> metrics, string name, double? value)
    {
        var availability = value.HasValue
            ? MetricAvailability.Available
            : NetworkAvailable ? MetricAvailability.WarmingUp : MetricAvailability.Unavailable;
        var reason = value.HasValue
            ? null
            : NetworkAvailable ? "Первый замер сетевых счётчиков" : "Нет активного сетевого подключения";
        metrics.Add(new TelemetryMetricSnapshot(name, value, "Б/с", "Windows Network Interface", CapturedAt, availability, reason));
    }

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

/// <summary>Считает загрузку CPU по дельтам системного времени Windows.</summary>
public sealed class CpuUsageTracker
{
    private ulong? idle,kernel,user;
    public double? Update(ulong i,ulong k,ulong u)
    {
        var pi=idle;var pk=kernel;var pu=user;idle=i;kernel=k;user=u;
        if(!pi.HasValue||!pk.HasValue||!pu.HasValue||i<pi||k<pk||u<pu)return null;
        var di=i-pi.Value;var dk=k-pk.Value;var du=u-pu.Value;
        if(dk>ulong.MaxValue-du)return null;var total=dk+du;
        return total==0||di>total?null:Math.Clamp((total-di)*100d/total,0,100);
    }
}

/// <summary>
/// Снимок ресурсов Windows. CPU — через GetSystemTimes; память/диск — Win32 / DriveInfo.
/// Collect вызывается по запросу Dashboard (и по таймеру на главной).
/// </summary>
public static class SystemMonitorService
{
    private static readonly object CpuLock = new();
    private static readonly CpuUsageTracker CpuTracker = new();
    public static SystemSnapshot Collect()
    {
        var cpuAvailable = TryCollectCpuUsage(out var cpuPct);

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
            PhysicalDiskActivity = PerformanceTelemetryService.CollectPhysicalDiskActivity(),
            GpuActivity = GpuTelemetryService.Collect(),
            TemperatureMetrics = TemperatureTelemetryService.Collect(),
            NetworkTraffic = PerformanceTelemetryService.CollectNetworkRate(),
            CapturedAt = DateTime.UtcNow,
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

    private static bool TryCollectCpuUsage(out double value)
    {
        value=0;
        if(!OperatingSystem.IsWindows())return false;
        lock(CpuLock)
        {
            if(!GetSystemTimes(out var idle,out var kernel,out var user))return false;
            var result=CpuTracker.Update(ToUInt64(idle),ToUInt64(kernel),ToUInt64(user));
            if(!result.HasValue)return false;
            value=result.Value;return true;
        }
    }
    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME t)=>
        ((ulong)(uint)t.dwHighDateTime<<32)|(uint)t.dwLowDateTime;
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel,out System.Runtime.InteropServices.ComTypes.FILETIME user);

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
