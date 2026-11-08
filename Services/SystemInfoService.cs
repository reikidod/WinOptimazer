using System.Globalization;
using System.Management;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public sealed record InfoLine(string Label, string Value);

public sealed record InfoSection(string Title, IReadOnlyList<InfoLine> Lines);

public interface ISystemInfoService
{
    Task<IReadOnlyList<InfoSection>> CollectAsync();
}

/// <summary>Собирает характеристики ПК. Сбой отдельного блока не ломает остальные.</summary>
public sealed partial class SystemInfoService : ISystemInfoService
{
    private const string NotAvailable = "н/д";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public Task<IReadOnlyList<InfoSection>> CollectAsync() => Task.Run(Collect);

    private static IReadOnlyList<InfoSection> Collect() =>
    [
        Section("Система", CollectOs),
        Section("Процессор", CollectCpu),
        Section("Оперативная память", CollectMemory),
        Section("Видеокарта", CollectGpu),
        Section("Накопители", CollectDisks),
        Section("Материнская плата и BIOS", CollectBoard),
        Section("Безопасность и питание", CollectSecurity),
    ];

    private static InfoSection Section(string title, Func<List<InfoLine>> collector)
    {
        var lines = new List<InfoLine>();
        try
        {
            lines.AddRange(collector());
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or SecurityException
                                        or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Logger.Error($"Не удалось собрать раздел «{title}»", ex);
            lines.Add(new InfoLine("Ошибка", $"Данные недоступны ({ex.Message})"));
        }

        return new InfoSection(title, lines);
    }

    private static List<InfoLine> CollectOs()
    {
        var lines = new List<InfoLine>();
        foreach (var os in Query(@"root\cimv2", "SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem"))
        {
            lines.Add(new InfoLine("Windows", Text(os["Caption"])));
            lines.Add(new InfoLine("Версия", $"{Text(os["Version"])} (сборка {Text(os["BuildNumber"])})"));
            lines.Add(new InfoLine("Разрядность", Text(os["OSArchitecture"])));
        }

        return lines;
    }

    private static List<InfoLine> CollectCpu()
    {
        var lines = new List<InfoLine>();
        var index = 1;
        var all = Query(@"root\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        foreach (var cpu in all)
        {
            var suffix = all.Count > 1 ? $" {index}" : string.Empty;
            lines.Add(new InfoLine($"Модель{suffix}", Text(cpu["Name"]).Trim()));
            lines.Add(new InfoLine($"Ядра / потоки{suffix}", $"{Text(cpu["NumberOfCores"])} / {Text(cpu["NumberOfLogicalProcessors"])}"));
            lines.Add(new InfoLine($"Макс. частота{suffix}", $"{Text(cpu["MaxClockSpeed"])} МГц"));
            index++;
        }

        return lines;
    }

    private static List<InfoLine> CollectMemory()
    {
        var lines = new List<InfoLine>();
        var total = Query(@"root\cimv2", "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        if (total is not null && TryToUInt64(total["TotalPhysicalMemory"], out var bytes))
        {
            lines.Add(new InfoLine("Всего", FormatBytes(bytes)));
        }

        foreach (var module in Query(@"root\cimv2",
                     "SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory"))
        {
            var capacity = TryToUInt64(module["Capacity"], out var size) ? FormatBytes(size) : NotAvailable;
            var speed = $"{Text(module["Speed"])} МГц (работает на {Text(module["ConfiguredClockSpeed"])} МГц)";
            var maker = $"{Text(module["Manufacturer"]).Trim()} {Text(module["PartNumber"]).Trim()}".Trim();
            lines.Add(new InfoLine(Text(module["DeviceLocator"]), $"{capacity}, {speed}, {maker}"));
        }

        return lines;
    }

    private static List<InfoLine> CollectGpu()
    {
        var lines = new List<InfoLine>();
        foreach (var gpu in Query(@"root\cimv2",
                     "SELECT Name, DriverVersion, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            var name = Text(gpu["Name"]);
            lines.Add(new InfoLine("Видеоадаптер", name));
            lines.Add(new InfoLine("Драйвер", Text(gpu["DriverVersion"])));

            // AdapterRAM — 32-битное поле и «обрезает» память больших карт на 4 ГБ, поэтому читаем из реестра.
            var video = ReadVideoMemoryBytes(name) ?? (TryToUInt64(gpu["AdapterRAM"], out var fallback) ? fallback : (ulong?)null);
            lines.Add(new InfoLine("Видеопамять", video is { } vram && vram > 0 ? FormatBytes(vram) : NotAvailable));

            if (gpu["CurrentHorizontalResolution"] is not null)
            {
                lines.Add(new InfoLine("Режим",
                    $"{Text(gpu["CurrentHorizontalResolution"])}×{Text(gpu["CurrentVerticalResolution"])} @ {Text(gpu["CurrentRefreshRate"])} Гц"));
            }
        }

        return lines;
    }

    private static List<InfoLine> CollectDisks()
    {
        var lines = new List<InfoLine>();
        var disks = Query(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk");
        foreach (var disk in disks)
        {
            var mediaType = ToUInt16(disk["MediaType"]) switch
            {
                3 => "HDD",
                4 => "SSD",
                5 => "SCM",
                _ => "тип неизвестен",
            };

            var bus = ToUInt16(disk["BusType"]) switch
            {
                17 => "NVMe",
                11 => "SATA",
                7 => "USB",
                _ => "шина " + Text(disk["BusType"]),
            };

            var size = TryToUInt64(disk["Size"], out var bytes) ? FormatBytes(bytes) : NotAvailable;
            lines.Add(new InfoLine(Text(disk["FriendlyName"]), $"{mediaType}, {bus}, {size}"));
        }

        return lines;
    }

    private static List<InfoLine> CollectBoard()
    {
        var lines = new List<InfoLine>();
        foreach (var board in Query(@"root\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard"))
        {
            lines.Add(new InfoLine("Плата", $"{Text(board["Manufacturer"]).Trim()} {Text(board["Product"]).Trim()}"));
        }

        foreach (var bios in Query(@"root\cimv2", "SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            lines.Add(new InfoLine("Версия BIOS", Text(bios["SMBIOSBIOSVersion"])));
            if (bios["ReleaseDate"] is string raw)
            {
                lines.Add(new InfoLine("Дата BIOS", ManagementDateTimeConverter.ToDateTime(raw).ToString("d", CultureInfo.CurrentCulture)));
            }
        }

        return lines;
    }

    private static List<InfoLine> CollectSecurity()
    {
        var lines = new List<InfoLine>();

        foreach (var guard in Query(@"root\Microsoft\Windows\DeviceGuard", "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard"))
        {
            var status = ToUInt16(guard["VirtualizationBasedSecurityStatus"]) switch
            {
                0 => "выключена",
                1 => "включена, но не запущена (нужна перезагрузка)",
                2 => "работает",
                _ => NotAvailable,
            };

            lines.Add(new InfoLine("VBS", status));

            // Код службы 2 = Hypervisor-enforced Code Integrity (целостность памяти).
            var hvciRunning = guard["SecurityServicesRunning"] is ushort[] services && services.Contains((ushort)2);
            lines.Add(new InfoLine("Целостность памяти (HVCI)", hvciRunning ? "работает" : "не работает"));
        }

        lines.Add(new InfoLine("Secure Boot", ReadSecureBoot()));

        var scheme = ProcessRunner.Run(System.IO.Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/getactivescheme");
        if (scheme.ExitCode == 0)
        {
            var match = SchemeNamePattern().Match(scheme.StandardOutput);
            lines.Add(new InfoLine("Схема питания", match.Success ? match.Groups[1].Value : scheme.StandardOutput.Trim()));
        }

        return lines;
    }

    private static string ReadSecureBoot()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", writable: false);
        return key?.GetValue("UEFISecureBootEnabled") switch
        {
            1 => "включён",
            0 => "выключен",
            _ => "не поддерживается (или BIOS в режиме Legacy)",
        };
    }

    private static ulong? ReadVideoMemoryBytes(string adapterName)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var classKey = baseKey.OpenSubKey(DisplayClassKey, writable: false);
        if (classKey is null)
        {
            return null;
        }

        foreach (var subKeyName in classKey.GetSubKeyNames())
        {
            // Служебные подразделы класса (например, «Configuration») могут быть закрыты даже для администратора:
            // один недоступный раздел не должен ломать весь блок «Видеокарта».
            try
            {
                using var adapter = classKey.OpenSubKey(subKeyName, writable: false);
                if (adapter?.GetValue("DriverDesc") as string != adapterName)
                {
                    continue;
                }

                var raw = adapter.GetValue("HardwareInformation.qwMemorySize");
                if (raw is long signed and > 0)
                {
                    return (ulong)signed;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }
        }

        return null;
    }

    /// <summary>Выполняет WMI-запрос и сразу materialize-ит результат, чтобы освободить объекты.</summary>
    private static List<ManagementBaseObject> Query(string scope, string query)
    {
        using var searcher = new ManagementObjectSearcher(scope, query);
        using var collection = searcher.Get();
        return collection.Cast<ManagementBaseObject>().ToList();
    }

    private static string Text(object? value) => value switch
    {
        null => NotAvailable,
        string s when string.IsNullOrWhiteSpace(s) => NotAvailable,
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? NotAvailable,
    };

    private static ushort ToUInt16(object? value) => value is ushort u ? u : (ushort)0xFFFF;

    private static bool TryToUInt64(object? value, out ulong result)
    {
        switch (value)
        {
            case ulong u:
                result = u;
                return true;
            case uint ui:
                result = ui;
                return true;
            case string s when ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    internal static string FormatBytes(ulong bytes)
    {
        const double gib = 1024d * 1024 * 1024;
        const double mib = 1024d * 1024;
        return bytes >= gib
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes / gib:0.#} ГБ")
            : string.Create(CultureInfo.CurrentCulture, $"{bytes / mib:0} МБ");
    }

    /// <summary>Текстовая версия отчёта для копирования в буфер обмена.</summary>
    public static string ToPlainText(IEnumerable<InfoSection> sections)
    {
        var builder = new StringBuilder();
        foreach (var section in sections)
        {
            builder.AppendLine($"[{section.Title}]");
            foreach (var line in section.Lines)
            {
                builder.AppendLine($"{line.Label}: {line.Value}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    [GeneratedRegex(@"\(([^)]+)\)")]
    private static partial Regex SchemeNamePattern();
}
