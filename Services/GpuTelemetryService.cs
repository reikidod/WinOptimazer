using System.Diagnostics;
using System.Globalization;

namespace WinOptimizer.Services;

public sealed record GpuAdapterActivitySnapshot(int AdapterIndex, double? UtilizationPercent, DateTime CapturedAt, bool IsWarmingUp = false)
{
    public string DisplayName => $"Графический адаптер {AdapterIndex + 1}";
    public bool HasUtilization => UtilizationPercent.HasValue;
    public bool ShowProgress => HasUtilization || IsWarmingUp;
    public string UtilizationText => UtilizationPercent is double value ? $"{value:0.#}%" : IsWarmingUp ? "Измерение…" : "н/д";
    public string StatusText => UtilizationPercent.HasValue ? $"Обновлено {CapturedAt.ToLocalTime():HH:mm:ss}" : IsWarmingUp ? "Подготовка счётчиков" : "Датчик недоступен";
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

    public static IReadOnlyList<GpuAdapterActivitySnapshot> Collect()
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            if (now - _lastRefreshUtc >= TimeSpan.FromSeconds(15))
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

            var values = new Dictionary<int, double>();
            var warming = new HashSet<int>();
            var seen = new HashSet<int>();
            var capturedAt = DateTime.UtcNow;
            foreach (var instance in _instances)
            {
                if (!TryGetAdapterIndex(instance, out var adapterIndex)) continue;
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
                    var utilization = Math.Clamp((double)value, 0, 100);
                    if (!values.TryGetValue(adapterIndex, out var current) || utilization > current) values[adapterIndex] = utilization;
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or ArgumentException)
                {
                    counter.Dispose();
                    Counters.Remove(instance);
                }
            }

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
