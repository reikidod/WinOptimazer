namespace WinOptimizer.Services;

/// <summary>Состояние измерения без подмены неизвестного значения нулём.</summary>
public enum MetricAvailability
{
    Available,
    WarmingUp,
    Unavailable,
}

/// <summary>Общий контракт метрики: значение, единица, источник, время и причина недоступности.</summary>
public sealed record TelemetryMetricSnapshot(
    string Name,
    double? Value,
    string Unit,
    string Source,
    DateTime CapturedAt,
    MetricAvailability Availability,
    string? UnavailableReason = null)
{
    public bool IsAvailable => Availability == MetricAvailability.Available && Value.HasValue;
    public string DisplayValue => IsAvailable ? $"{Value:0.#} {Unit}" : Availability == MetricAvailability.WarmingUp ? "Измерение…" : "н/д";
    public string StatusText => IsAvailable
        ? $"{Source} · {CapturedAt.ToLocalTime():HH:mm:ss}"
        : UnavailableReason ?? (Availability == MetricAvailability.WarmingUp ? "Подготовка источника" : "Источник недоступен");
}
