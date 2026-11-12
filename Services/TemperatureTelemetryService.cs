using System.Globalization;
using System.Management;

namespace WinOptimizer.Services;

public sealed record TemperatureMetricSnapshot(
    string Category,
    string Name,
    double? Celsius,
    string Source,
    DateTime CapturedAt,
    MetricAvailability Availability,
    string? UnavailableReason = null)
{
    public bool IsAvailable => Availability == MetricAvailability.Available && Celsius.HasValue;
    public string DisplayValue => IsAvailable ? $"{Celsius:0.#} °C" : Availability == MetricAvailability.WarmingUp ? "Измерение…" : "н/д";
    public string StatusText => IsAvailable
        ? $"{Source} · {CapturedAt.ToLocalTime():HH:mm:ss}"
        : UnavailableReason ?? "Датчик недоступен";

    public TelemetryMetricSnapshot ToMetricSnapshot() => new(
        Name, Celsius, "°C", Source, CapturedAt, Availability, UnavailableReason);
}

public static class TemperatureTelemetryService
{
    private const double MinimumTemperatureCelsius = -20;
    private const double MaximumTemperatureCelsius = 150;
    private static readonly object Sync = new();
    private static readonly string[] Providers = ["root\\LibreHardwareMonitor", "root\\OpenHardwareMonitor"];
    private static IReadOnlyList<TemperatureMetricSnapshot> _cached = Array.Empty<TemperatureMetricSnapshot>();
    private static DateTime _lastCollectedUtc = DateTime.MinValue;

    public static bool TryClassifySensor(string? identifier, string? name, out string category)
    {
        category = string.Empty;
        var key = $"{identifier} {name}".ToLowerInvariant();

        if (key.Contains("nvme", StringComparison.Ordinal) || key.Contains("ssd", StringComparison.Ordinal) ||
            key.Contains("storage", StringComparison.Ordinal) || key.Contains("drive", StringComparison.Ordinal))
        {
            category = "NVMe / накопитель";
            return true;
        }

        if (key.Contains("gpu", StringComparison.Ordinal) || key.Contains("nvidia", StringComparison.Ordinal) ||
            key.Contains("radeon", StringComparison.Ordinal) || key.Contains("graphics", StringComparison.Ordinal))
        {
            category = "GPU";
            return true;
        }

        if (key.Contains("cpu", StringComparison.Ordinal) || key.Contains("intelcpu", StringComparison.Ordinal) ||
            key.Contains("amdcpu", StringComparison.Ordinal) || key.Contains("processor", StringComparison.Ordinal))
        {
            category = "CPU";
            return true;
        }

        return false;
    }

    public static IReadOnlyList<TemperatureMetricSnapshot> Collect()
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            if (now - _lastCollectedUtc < TimeSpan.FromSeconds(5))
                return _cached;

            var result = new List<TemperatureMetricSnapshot>();
            foreach (var provider in Providers)
            {
                var providerName = provider.Contains("LibreHardwareMonitor", StringComparison.Ordinal)
                    ? "LibreHardwareMonitor"
                    : "OpenHardwareMonitor";

                try
                {
                    var options = new ConnectionOptions { Timeout = TimeSpan.FromSeconds(2) };
                    var scope = new ManagementScope($@"\\.\{provider}", options);
                    scope.Connect();
                    var enumeration = new EnumerationOptions
                    {
                        Timeout = TimeSpan.FromSeconds(2),
                        ReturnImmediately = true,
                    };

                    using var searcher = new ManagementObjectSearcher(
                        scope,
                        new ObjectQuery("SELECT Identifier, Name, SensorType, Value FROM Sensor"),
                        enumeration);
                    using var sensors = searcher.Get();

                    foreach (ManagementObject sensor in sensors)
                    {
                        using (sensor)
                        {
                            var sensorType = Convert.ToString(sensor["SensorType"], CultureInfo.InvariantCulture);
                            if (!string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var identifier = Convert.ToString(sensor["Identifier"], CultureInfo.InvariantCulture) ?? string.Empty;
                            var name = Convert.ToString(sensor["Name"], CultureInfo.InvariantCulture) ?? "Температурный датчик";
                            if (!TryClassifySensor(identifier, name, out var category))
                                continue;

                            double? value = null;
                            try
                            {
                                if (sensor["Value"] is not null)
                                {
                                    var parsed = Convert.ToDouble(sensor["Value"], CultureInfo.InvariantCulture);
                                    if (double.IsFinite(parsed) && parsed is >= MinimumTemperatureCelsius and <= MaximumTemperatureCelsius)
                                        value = parsed;
                                }
                            }
                            catch (FormatException) { }
                            catch (InvalidCastException) { }

                            var availability = value.HasValue ? MetricAvailability.Available : MetricAvailability.Unavailable;
                            var key = $"{providerName}|{identifier}";
                            if (result.Any(item => string.Equals($"{item.Source}|{item.Name}", key, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            result.Add(new TemperatureMetricSnapshot(
                                category,
                                string.IsNullOrWhiteSpace(name) ? category : name.Trim(),
                                value,
                                providerName,
                                now,
                                availability,
                                value.HasValue ? null : "Провайдер не вернул корректное значение температуры"));
                        }
                    }
                }
                catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException)
                {
                    // Отсутствие необязательного провайдера не является ошибкой сбора остальных метрик.
                }
            }

            _cached = result
                .GroupBy(item => $"{item.Category}|{item.Name}|{item.Source}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _lastCollectedUtc = now;
            return _cached;
        }
    }

    public static void Dispose()
    {
        lock (Sync)
        {
            _cached = Array.Empty<TemperatureMetricSnapshot>();
            _lastCollectedUtc = DateTime.MinValue;
        }
    }
}
