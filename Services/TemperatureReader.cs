using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

/// <summary>
/// Температуры комплектующих. Только чтение штатных источников Windows (счётчики тепловых зон, WMI накопителей,
/// nvidia-smi из драйвера NVIDIA). Никаких сторонних драйверов и записи в систему: показания и датчики Windows
/// не затрагиваются. Каждый источник изолирован — сбой одного не прячет остальные.
/// </summary>
internal static class TemperatureReader
{
    private const double MinPlausibleC = -20;
    private const double MaxPlausibleC = 150;

    public static List<InfoLine> Collect()
    {
        var lines = new List<InfoLine>();
        ReadSafely(lines, "тепловые зоны", ReadThermalZones);
        ReadSafely(lines, "NVIDIA", ReadNvidia);
        ReadSafely(lines, "накопители", ReadDisks);

        if (lines.Count == 0)
        {
            lines.Add(new InfoLine("Датчики", "Windows не отдаёт температуры на этом ПК"));
        }

        return lines;
    }

    /// <summary>Температура из значения в десятых долях Кельвина (HighPrecisionTemperature / MSAcpi) или в Кельвинах.</summary>
    internal static double? FromDeciKelvin(object? value) => ToCelsius(AsDouble(value) / 10d);

    internal static double? FromKelvin(object? value) => ToCelsius(AsDouble(value));

    private static double? ToCelsius(double? kelvin)
    {
        if (kelvin is not { } k)
        {
            return null;
        }

        var celsius = k - 273.15;
        return celsius is >= MinPlausibleC and <= MaxPlausibleC ? celsius : null;
    }

    private static double? AsDouble(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    internal static string Format(double celsius) =>
        string.Create(CultureInfo.CurrentCulture, $"{celsius:0} °C");

    /// <summary>Разбор вывода «nvidia-smi --query-gpu=name,temperature.gpu --format=csv,noheader,nounits».</summary>
    internal static List<(string Name, double Celsius)> ParseNvidiaSmi(string output)
    {
        var result = new List<(string, double)>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var comma = raw.LastIndexOf(',');
            if (comma <= 0)
            {
                continue;
            }

            var name = raw[..comma].Trim();
            var temp = raw[(comma + 1)..].Trim();
            if (name.Length > 0
                && double.TryParse(temp, NumberStyles.Float, CultureInfo.InvariantCulture, out var celsius)
                && celsius is >= MinPlausibleC and <= MaxPlausibleC)
            {
                result.Add((name, celsius));
            }
        }

        return result;
    }

    /// <summary>«\_TZ.TZ00» → «TZ00».</summary>
    internal static string ZoneName(string raw)
    {
        var trimmed = raw.Trim().TrimStart('\\');
        var dot = trimmed.LastIndexOf('.');
        return dot >= 0 && dot < trimmed.Length - 1 ? trimmed[(dot + 1)..] : trimmed;
    }

    private static void ReadSafely(List<InfoLine> lines, string source, Func<IEnumerable<InfoLine>> read)
    {
        try
        {
            lines.AddRange(read());
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or SecurityException
                                       or InvalidOperationException or IOException or TimeoutException
                                       or COMException or System.ComponentModel.Win32Exception)
        {
            // Нет датчика или данных — не ошибка: просто не показываем строку.
            Logger.Error($"Температуры: источник «{source}» недоступен", ex);
        }
    }

    private static IEnumerable<InfoLine> ReadThermalZones()
    {
        // Не все версии Windows и OEM-прошивки публикуют этот performance-класс.
        // WMI может выбросить ManagementException именно при перечислении коллекции,
        // поэтому каждый источник читается в отдельном try/catch.
        try
        {
            var performanceZones = ReadThermalZoneQuery(
                @"root\cimv2",
                "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation",
                "Name",
                "HighPrecisionTemperature",
                "Temperature");

            if (performanceZones.Count > 0)
            {
                return performanceZones;
            }
        }
        catch (ManagementException)
        {
            // Источник не поддерживается — пробуем ACPI ниже.
        }

        try
        {
            return ReadThermalZoneQuery(
                @"root\WMI",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature",
                "InstanceName",
                "CurrentTemperature",
                null);
        }
        catch (ManagementException)
        {
            // На некоторых устройствах Windows не предоставляет ни один WMI-датчик.
            return [];
        }
    }

    private static List<InfoLine> ReadThermalZoneQuery(
        string scope,
        string query,
        string nameProperty,
        string temperatureProperty,
        string? fallbackTemperatureProperty)
    {
        var result = new List<InfoLine>();
        foreach (var zone in SystemInfoService.Query(scope, query))
        {
            var celsius = FromDeciKelvin(zone[temperatureProperty]);
            if (celsius is null && fallbackTemperatureProperty is not null)
            {
                celsius = FromKelvin(zone[fallbackTemperatureProperty]);
            }

            if (celsius is { } value)
            {
                var name = Convert.ToString(zone[nameProperty], CultureInfo.InvariantCulture) ?? "ACPI";
                result.Add(new InfoLine($"Тепловая зона {ZoneName(name)}", Format(value)));
            }
        }

        return result;
    }

    private static IEnumerable<InfoLine> ReadNvidia()
    {
        var exe = FindNvidiaSmi();
        if (exe is null)
        {
            return [];
        }

        var run = ProcessRunner.Run(exe, "--query-gpu=name,temperature.gpu --format=csv,noheader,nounits", timeoutMs: 5_000);
        return run.ExitCode == 0 ? NvidiaInfoLines(run.StandardOutput) : [];
    }

    internal static List<InfoLine> NvidiaInfoLines(string output) =>
        ParseNvidiaSmi(output)
            .Select(g => new InfoLine("GPU · " + g.Name, Format(g.Celsius)))
            .ToList();

    private static string? FindNvidiaSmi()
    {
        string[] candidates =
        [
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<InfoLine> ReadDisks()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var disk in SystemInfoService.Query(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, FriendlyName FROM MSFT_PhysicalDisk"))
        {
            var id = Convert.ToString(disk["DeviceId"], CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(id))
            {
                names.TryAdd(id, Convert.ToString(disk["FriendlyName"], CultureInfo.InvariantCulture)?.Trim() ?? $"Диск {id}");
            }
        }

        var result = new List<InfoLine>();
        foreach (var counter in SystemInfoService.Query(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, Temperature FROM MSFT_StorageReliabilityCounter"))
        {
            // 0 — накопитель не сообщает температуру.
            if (AsDouble(counter["Temperature"]) is not { } c || c is <= 0 or > MaxPlausibleC)
            {
                continue;
            }

            var id = Convert.ToString(counter["DeviceId"], CultureInfo.InvariantCulture) ?? string.Empty;
            result.Add(new InfoLine(names.TryGetValue(id, out var name) ? name : $"Диск {id}", Format(c)));
        }

        return result;
    }
}
