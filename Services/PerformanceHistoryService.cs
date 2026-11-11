using System.Collections.Generic;

namespace WinOptimizer.Services;

/// <summary>Один исторический замер ресурсов. Недоступная загрузка CPU остаётся неизвестной, а не превращается в 0%.</summary>
public sealed record PerformanceHistoryPoint(DateTime CapturedAt, double? CpuPercent, double MemoryPercent)
{
    public double CpuBarHeight => CpuPercent is double value ? Math.Clamp(value, 0d, 100d) * 0.48d : 2d;
    public double CpuBarOpacity => CpuPercent.HasValue ? 1d : 0.22d;
    public double MemoryBarHeight => Math.Clamp(MemoryPercent, 0d, 100d) * 0.48d;
    public string TimeLabel => CapturedAt.ToString("HH:mm:ss");
    public string CpuValueText => CpuPercent is double value ? $"CPU {value:0}%" : "CPU недоступен";
    public string MemoryValueText => $"RAM {MemoryPercent:0}%";
    public string TooltipText => $"{TimeLabel} · {CpuValueText} · {MemoryValueText}";
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

        var point = new PerformanceHistoryPoint(
            capturedAt ?? DateTime.Now,
            snapshot.CpuAvailable ? Math.Clamp(snapshot.CpuPercent, 0d, 100d) : null,
            Math.Clamp((double)snapshot.MemoryLoadPercent, 0d, 100d));

        _samples.Enqueue(point);
        while (_samples.Count > _capacity)
            _samples.Dequeue();

        return point;
    }
}
