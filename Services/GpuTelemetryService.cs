using System.Diagnostics;
using System.Globalization;

namespace WinOptimizer.Services;

public sealed record GpuEngineUtilizationSample(int AdapterIndex, int EngineIndex, string EngineType, double UtilizationPercent);

public sealed record GpuAdapterActivitySnapshot(int AdapterIndex, double? UtilizationPercent, DateTime CapturedAt, bool IsWarmingUp = false)
{
    public string DisplayName => $"Графический адаптер {AdapterIndex + 1}";
    public bool HasUtilization => UtilizationPercent.HasValue;
    public bool ShowProgress => HasUtilization || IsWarmingUp;
    public string UtilizationText => UtilizationPercent is double value ? $"{value:0.#}%" : IsWarmingUp ? "Измерение…" : "н/д";
    public string StatusText => UtilizationPercent.HasValue ? $"Обновлено {CapturedAt.ToLocalTime():HH:mm:ss}" : IsWarmingUp ? "Подготовка счётчиков" : "Датчик недоступен";
    public TelemetryMetricSnapshot ToMetricSnapshot() => new(
        DisplayName, UtilizationPercent, "%", "Windows GPU Engine", CapturedAt,
        UtilizationPercent.HasValue ? MetricAvailability.Available : IsWarmingUp ? MetricAvailability.WarmingUp : MetricAvailability.Unavailable,
        UtilizationPercent.HasValue ? null : IsWarmingUp ? "Первый замер счётчика" : "Счётчики GPU Engine недоступны");
}

public static class GpuTelemetryService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, PerformanceCounter> Counters = new(StringComparer.OrdinalIgnoreCase);
    private static string[] _instances = Array.Empty<string>();
    private static DateTime _lastRefreshUtc = DateTime.MinValue;

    public static bool TryGetAdapterIndex(string? instanceName, out int adapterIndex)
    {
        adapterIndex = -1;
        if (string.IsNullOrWhiteSpace(instanceName)) return false;
        const string marker = "_phys_";
        var markerIndex = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return false;
        var start = markerIndex + marker.Length;
        var end = instanceName.IndexOf('_', start);
        var token = end < 0 ? instanceName[start..] : instanceName[start..end];
        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out adapterIndex) && adapterIndex >= 0;
    }

    public static bool TryGetEngineIndex(string? instanceName, out int engineIndex)
    {
        engineIndex = -1;
        if (string.IsNullOrWhiteSpace(instanceName))
            return false;

        const string marker = "_eng_";
        const string engineTypeMarker = "_engtype_";
        var markerIndex = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;

        var start = markerIndex + marker.Length;
        var end = instanceName.IndexOf(engineTypeMarker, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return false;

        return int.TryParse(instanceName[start..end], NumberStyles.None, CultureInfo.InvariantCulture, out engineIndex)
            && engineIndex >= 0;
    }

    public static bool TryGetEngineType(string? instanceName, out string engineType)
    {
        engineType = string.Empty;
        if (string.IsNullOrWhiteSpace(instanceName))
            return false;

        const string marker = "_engtype_";
        var markerIndex = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0 || markerIndex + marker.Length >= instanceName.Length)
            return false;

        engineType = instanceName[(markerIndex + marker.Length)..].Trim();
        return engineType.Length > 0;
    }

    public static IReadOnlyDictionary<int, double> AggregateUtilization(IEnumerable<GpuEngineUtilizationSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var engineTotals = new Dictionary<(int AdapterIndex, string EngineType), double>();
        foreach (var sample in samples)
        {
            if (sample.AdapterIndex < 0 || sample.EngineIndex < 0 || string.IsNullOrWhiteSpace(sample.EngineType) ||
                !double.IsFinite(sample.UtilizationPercent) || sample.UtilizationPercent < 0)
                continue;

            var key = (sample.AdapterIndex, sample.EngineIndex);
            engineTotals[key] = Math.Min(100, engineTotals.GetValueOrDefault(key) + sample.UtilizationPercent);
        }

        return engineTotals
            .GroupBy(pair => pair.Key.AdapterIndex)
            .ToDictionary(group => group.Key, group => group.Max(pair => pair.Value));
    }

    public static IReadOnlyList<GpuAdapterActivitySnapshot> Collect()
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            if (now - _lastRefreshUtc >= PerformanceTelemetryPolicy.DeviceEnumerationRefreshInterval)
            {
                try
                {
                    _instances = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                        .Where(name => TryGetAdapterIndex(name, out _)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or ArgumentException)
                {
                    _lastRefreshUtc = now;
                    if (_instances.Length == 0) return Array.Empty<GpuAdapterActivitySnapshot>();
                }
                _lastRefreshUtc = now;
            }

            var active = _instances.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in Counters.Keys.Where(name => !active.Contains(name)).ToArray())
            {
                Counters[stale].Dispose();
                Counters.Remove(stale);
            }

            var samples = new List<GpuEngineUtilizationSample>();
            var warming = new HashSet<int>();
            var seen = new HashSet<int>();
            var capturedAt = DateTime.UtcNow;
            foreach (var instance in _instances)
            {
                if (!TryGetAdapterIndex(instance, out var adapterIndex) || !TryGetEngineIndex(instance, out var engineIndex) || !TryGetEngineType(instance, out var engineType)) continue;
                seen.Add(adapterIndex);
                if (!Counters.TryGetValue(instance, out var counter))
                {
                    try
                    {
                        counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                        _ = counter.NextValue();
                        Counters.Add(instance, counter);
                        warming.Add(adapterIndex);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or ArgumentException)
                    {
                        counter?.Dispose();
                    }
                    continue;
                }

                try
                {
                    var value = counter.NextValue();
                    if (!float.IsFinite(value) || value < 0) continue;
                    samples.Add(new GpuEngineUtilizationSample(adapterIndex, engineIndex, engineType, Math.Clamp((double)value, 0, 100)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or ArgumentException)
                {
                    counter.Dispose();
                    Counters.Remove(instance);
                }
            }

            var values = AggregateUtilization(samples);
            return seen.OrderBy(index => index).Select(index => new GpuAdapterActivitySnapshot(
                index, values.TryGetValue(index, out var value) ? value : null, capturedAt,
                !values.ContainsKey(index) && warming.Contains(index))).ToArray();
        }
    }

    public static void Dispose()
    {
        lock (Sync)
        {
            foreach (var counter in Counters.Values) counter.Dispose();
            Counters.Clear();
            _instances = Array.Empty<string>();
            _lastRefreshUtc = DateTime.MinValue;
        }
    }
}
