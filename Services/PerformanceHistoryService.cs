using System.Collections.Generic;

namespace WinOptimizer.Services;

/// <summary>Один замер ресурсов. Недоступные датчики остаются null и не рисуются как нулевые значения.</summary>
public sealed record PerformanceHistoryPoint(
    DateTime CapturedAt,
    double? CpuPercent,
    double MemoryPercent,
    double? GpuPercent,
    double? DiskReadBytesPerSecond,
    double? DiskWriteBytesPerSecond,
    double? NetworkDownloadBytesPerSecond,
    double? NetworkUploadBytesPerSecond)
{
    private const double RateScaleBytesPerSecond = 100d * 1024 * 1024;
    private const double ChartHeight = 48d;

    public double CpuBarHeight => PercentBarHeight(CpuPercent);
    public double CpuBarOpacity => CpuPercent.HasValue ? 1d : 0.22d;
    public double MemoryBarHeight => PercentBarHeight(MemoryPercent);
    public double GpuBarHeight => PercentBarHeight(GpuPercent);
    public double GpuBarOpacity => GpuPercent.HasValue ? 1d : 0.22d;
    public double DiskBarHeight => RateBarHeight(CombinedRate(DiskReadBytesPerSecond, DiskWriteBytesPerSecond));
    public double DiskBarOpacity => DiskReadBytesPerSecond.HasValue && DiskWriteBytesPerSecond.HasValue ? 1d : 0.22d;
    public double NetworkBarHeight => RateBarHeight(CombinedRate(NetworkDownloadBytesPerSecond, NetworkUploadBytesPerSecond));
    public double NetworkBarOpacity => NetworkDownloadBytesPerSecond.HasValue && NetworkUploadBytesPerSecond.HasValue ? 1d : 0.22d;

    public string TimeLabel => CapturedAt.ToString("HH:mm:ss");
    public string CpuValueText => CpuPercent is double value ? $"CPU {value:0}%" : "CPU недоступен";
    public string MemoryValueText => $"RAM {MemoryPercent:0}%";
    public string GpuValueText => GpuPercent is double value ? $"GPU пик {value:0.#}%" : "GPU н/д";
    public string DiskValueText => DiskReadBytesPerSecond is double read && DiskWriteBytesPerSecond is double write
        ? $"Диск ↓ {FormatRate(read)} · ↑ {FormatRate(write)}"
        : "Диск н/д";
    public string NetworkValueText => NetworkDownloadBytesPerSecond is double download && NetworkUploadBytesPerSecond is double upload
        ? $"Сеть ↓ {FormatRate(download)} · ↑ {FormatRate(upload)}"
        : "Сеть н/д";
    public string TooltipText => $"{TimeLabel} · {CpuValueText} · {MemoryValueText} · {GpuValueText} · {DiskValueText} · {NetworkValueText}";

    private static double PercentBarHeight(double? value) =>
        value is double percent ? Math.Clamp(percent, 0d, 100d) * (ChartHeight / 100d) : 2d;

    private static double PercentBarHeight(double value) =>
        Math.Clamp(value, 0d, 100d) * (ChartHeight / 100d);

    private static double? CombinedRate(double? first, double? second) =>
        first is double a && second is double b ? a + b : null;

    private static double RateBarHeight(double? bytesPerSecond)
    {
        if (bytesPerSecond is not double value || !double.IsFinite(value) || value < 0)
            return 2d;

        // Логарифмическая шкала сохраняет различимость небольших скоростей и не насыщается от редких пиков.
        var normalized = Math.Log10(1d + value) / Math.Log10(1d + RateScaleBytesPerSecond);
        return Math.Clamp(normalized, 0d, 1d) * ChartHeight;
    }

    private static string FormatRate(double bytesPerSecond)
    {
        string[] units = ["Б/с", "КБ/с", "МБ/с", "ГБ/с", "ТБ/с"];
        var value = Math.Max(0, bytesPerSecond);
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
/// Ограниченная локальная история показателей для Performance Center.
/// Хранит только данные текущего сеанса приложения и не записывает их в файлы или сеть.
/// </summary>
public sealed class PerformanceHistoryService
{
    public const int DefaultCapacity = 60;

    private readonly Queue<PerformanceHistoryPoint> _samples = new();
    private readonly int _capacity;

    public PerformanceHistoryService(int capacity = DefaultCapacity)
    {
        if (capacity is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Вместимость истории должна быть от 1 до 10000 замеров.");

        _capacity = capacity;
    }

    public IReadOnlyList<PerformanceHistoryPoint> Samples => _samples.ToArray();

    public PerformanceHistoryPoint Add(SystemSnapshot snapshot, DateTime? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var gpuValues = snapshot.GpuActivity
            .Where(item => item.UtilizationPercent is double value && double.IsFinite(value) && value >= 0)
            .Select(item => Math.Clamp(item.UtilizationPercent!.Value, 0d, 100d))
            .ToArray();
        double? gpuPeak = gpuValues.Length == 0 ? null : gpuValues.Max();

        var diskRead = SumWhenComplete(snapshot.PhysicalDiskActivity, item => item.ReadBytesPerSecond);
        var diskWrite = SumWhenComplete(snapshot.PhysicalDiskActivity, item => item.WriteBytesPerSecond);
        var networkDownload = ValidRate(snapshot.NetworkTraffic.ReceivedBytesPerSecond);
        var networkUpload = ValidRate(snapshot.NetworkTraffic.SentBytesPerSecond);

        var point = new PerformanceHistoryPoint(
            capturedAt ?? snapshot.CapturedAt.ToLocalTime(),
            snapshot.CpuAvailable && double.IsFinite(snapshot.CpuPercent) && snapshot.CpuPercent >= 0
                ? Math.Clamp(snapshot.CpuPercent, 0d, 100d)
                : null,
            Math.Clamp((double)snapshot.MemoryLoadPercent, 0d, 100d),
            gpuPeak,
            diskRead,
            diskWrite,
            networkDownload,
            networkUpload);

        _samples.Enqueue(point);
        while (_samples.Count > _capacity)
            _samples.Dequeue();

        return point;
    }

    private static double? SumWhenComplete<T>(IReadOnlyList<T> samples, Func<T, double?> selector)
    {
        if (samples.Count == 0)
            return null;

        var total = 0d;
        foreach (var sample in samples)
        {
            var value = selector(sample);
            if (value is not double current || !double.IsFinite(current) || current < 0)
                return null;

            total += current;
            if (!double.IsFinite(total))
                return null;
        }

        return total;
    }

    private static double? ValidRate(double? value) =>
        value is double rate && double.IsFinite(rate) && rate >= 0 ? rate : null;
}
