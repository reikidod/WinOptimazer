using System.Management;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public interface IHardwareProfileService
{
    Task<HardwareProfile> CollectAsync(CancellationToken ct = default);
}

/// <summary>Определяет процессор, память, основную видеокарту и режим экрана (через WMI).</summary>
public sealed class HardwareProfileService : IHardwareProfileService
{
    private static readonly string[] VirtualAdapters =
        { "microsoft basic", "remote", "virtual", "parsec", "citrix", "mirage", "idd", "displaylink", "vmware", "hyper-v" };

    public Task<HardwareProfile> CollectAsync(CancellationToken ct = default) => Task.Run(Collect, ct);

    private static HardwareProfile Collect()
    {
        string cpuName = "", gpuName = "";
        int cores = 0, threads = 0, w = 0, h = 0, hz = 0;
        double ghz = 0, ram = 0, vram = 0;

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (var o in s.Get())
            {
                cpuName = (o["Name"] as string ?? "").Trim();
                cores += ToInt(o["NumberOfCores"]);
                threads += ToInt(o["NumberOfLogicalProcessors"]);
                ghz = Math.Max(ghz, ToInt(o["MaxClockSpeed"]) / 1000.0);
            }
        }
        catch (Exception ex) { Logger.Error("Профиль ПК: процессор", ex); }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var o in s.Get())
                ram = Convert.ToDouble(o["TotalPhysicalMemory"] ?? 0UL) / (1024.0 * 1024 * 1024);
        }
        catch (Exception ex) { Logger.Error("Профиль ПК: память", ex); }

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
            var best = -1;
            foreach (var o in s.Get())
            {
                var name = (o["Name"] as string ?? "").Trim();
                var lower = name.ToLowerInvariant();
                if (name.Length == 0 || VirtualAdapters.Any(v => lower.Contains(v))) continue;

                // Экран может быть подключён к встроенной графике, а игра идёт на дискретной: режим берём максимальный из найденных.
                var ow = ToInt(o["CurrentHorizontalResolution"]);
                var oh = ToInt(o["CurrentVerticalResolution"]);
                if (ow * (long)oh > w * (long)h) { w = ow; h = oh; hz = ToInt(o["CurrentRefreshRate"]); }
                else if (ow == w && oh == h) hz = Math.Max(hz, ToInt(o["CurrentRefreshRate"]));

                var rank = Rank(name);
                if (rank <= best) continue;
                best = rank;
                gpuName = name;
                var bytes = SystemInfoService.ReadVideoMemoryBytes(name)
                            ?? (o["AdapterRAM"] is not null ? Convert.ToUInt64(o["AdapterRAM"]) : 0UL);
                vram = bytes / (1024.0 * 1024 * 1024);
            }
        }
        catch (Exception ex) { Logger.Error("Профиль ПК: видеокарта", ex); }

        if (w <= 0 || h <= 0) { w = 1920; h = 1080; }
        if (hz <= 1) hz = 60;
        return new HardwareProfile(cpuName, cores, threads, ghz, Math.Round(ram, 1), gpuName, Math.Round(vram, 1), w, h, hz);
    }

    /// <summary>Дискретная карта важнее встроенной.</summary>
    internal static int Rank(string name) => GpuInfo.IsIntegrated(name) ? 1 : 3;

    private static int ToInt(object? v)
    {
        try { return v is null ? 0 : Convert.ToInt32(v); }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException) { return 0; }
    }
}
