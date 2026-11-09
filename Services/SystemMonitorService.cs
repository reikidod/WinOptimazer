using System.Diagnostics;
using System.Windows.Threading;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

/// <summary>Снимок нагрузки для Dashboard. Не блокирует UI.</summary>
public sealed record SystemSnapshot(
    double CpuPercent,
    double RamUsedPercent,
    string? RamDetail,
    string? CpuTemp,
    string? GpuTemp,
    DateTime TakenUtc);

public interface ISystemMonitorService : IDisposable
{
    SystemSnapshot? Last { get; }
    event Action<SystemSnapshot>? Updated;
    void Start(TimeSpan interval);
    void Stop();
    Task<SystemSnapshot> SampleOnceAsync();
}

/// <summary>Один общий опрос CPU/RAM вместо нескольких независимых таймеров на страницах.</summary>
public sealed class SystemMonitorService : ISystemMonitorService
{
    private readonly PerformanceCounter? _cpu;
    private DispatcherTimer? _timer;
    private bool _disposed;

    public SystemSnapshot? Last { get; private set; }

    public event Action<SystemSnapshot>? Updated;

    public SystemMonitorService()
    {
        try
        {
            _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _ = _cpu.NextValue(); // первый вызов всегда 0
        }
        catch (Exception ex)
        {
            Logger.Error("PerformanceCounter CPU недоступен", ex);
            _cpu = null;
        }
    }

    public void Start(TimeSpan interval)
    {
        if (_disposed) return;
        Stop();
        _timer = new DispatcherTimer { Interval = interval < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : interval };
        _timer.Tick += async (_, _) =>
        {
            try
            {
                var snap = await SampleOnceAsync().ConfigureAwait(true);
                Last = snap;
                Updated?.Invoke(snap);
            }
            catch (Exception ex)
            {
                Logger.Error("Ошибка мониторинга", ex);
            }
        };
        _timer.Start();
    }

    public void Stop()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= null!;
        _timer = null;
    }

    public Task<SystemSnapshot> SampleOnceAsync() => Task.Run(SampleOnce);

    private SystemSnapshot SampleOnce()
    {
        double cpu = 0;
        try { cpu = _cpu?.NextValue() ?? 0; } catch { /* ignore */ }

        double ramPct = 0;
        string? ramDetail = null;
        try
        {
            var info = GC.GetGCMemoryInfo();
            var total = (double)info.TotalAvailableMemoryBytes;
            // TotalAvailableMemoryBytes ≈ физическая RAM; занятость через WorkingSet процесса неполная,
            // поэтому используем GlobalMemoryStatusEx через простой WMI-free путь: PerformanceCounter Memory.
            using var avail = new PerformanceCounter("Memory", "Available MBytes", readOnly: true);
            var availMb = avail.NextValue();
            var totalMb = total / (1024d * 1024d);
            if (totalMb > 1)
            {
                ramPct = Math.Clamp(100.0 * (1.0 - availMb / totalMb), 0, 100);
                ramDetail = $"{availMb:0} МБ свободно из ~{totalMb:0} МБ";
            }
        }
        catch (Exception ex)
        {
            Logger.Error("RAM sample", ex);
        }

        string? cpuTemp = null;
        string? gpuTemp = null;
        try
        {
            foreach (var line in TemperatureReader.Collect())
            {
                if (line.Label.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                    line.Label.Contains("ЦП", StringComparison.OrdinalIgnoreCase))
                    cpuTemp ??= line.Value;
                if (line.Label.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                    line.Label.Contains("Видео", StringComparison.OrdinalIgnoreCase))
                    gpuTemp ??= line.Value;
            }
        }
        catch { /* температуры опциональны */ }

        return new SystemSnapshot(cpu, ramPct, ramDetail, cpuTemp, gpuTemp, DateTime.UtcNow);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cpu?.Dispose();
    }
}
