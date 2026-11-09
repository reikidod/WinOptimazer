using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WinOptimizer.Services;

/// <summary>Читаемое название схемы питания: напрямую из Windows (Юникод) либо из вывода «powercfg /getactivescheme».</summary>
internal static partial class PowerPlanNames
{
    // Стандартные схемы Windows: названия не зависят от языка системы и кодировки консоли.
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["381b4222-f694-41f0-9685-ff5bb260df2e"] = "Сбалансированная",
        ["8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"] = "Высокая производительность",
        ["a1841308-3541-4fab-bc81-f71556f20b4a"] = "Экономия энергии",
        ["e9a42b02-d5df-448d-aa00-03f14749eb61"] = "Максимальная производительность",
    };

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid,
        IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// Название активной схемы без консоли и кодовых страниц: GUID и имя читаются через powrprof.dll в Юникоде,
    /// поэтому русские названия не превращаются в «кракозябры». null — если системный вызов недоступен.
    /// </summary>
    public static string? ReadActive()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
            try
            {
                var guid = Marshal.PtrToStructure<Guid>(ptr);
                var id = guid.ToString();
                if (Known.TryGetValue(id, out var known)) return known;
                return ReadFriendlyName(guid) ?? id;
            }
            finally
            {
                LocalFree(ptr);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException
                                       or MarshalDirectiveException or AccessViolationException)
        {
            return null;
        }
    }

    private static string? ReadFriendlyName(Guid guid)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size < 2 || size > 4096) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return null;
            var name = Marshal.PtrToStringUni(buffer)?.Trim();
            // «@%SystemRoot%\system32\powrprof.dll,-N» — нераскрытая ссылка на ресурс: такое имя читать нельзя.
            return string.IsNullOrWhiteSpace(name) || name.StartsWith('@') ? null : name;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string Describe(string powercfgOutput)
    {
        var guid = GuidPattern().Match(powercfgOutput);
        if (guid.Success && Known.TryGetValue(guid.Value, out var known))
        {
            return known;
        }

        // Своя схема (например от AMD или производителя ноутбука): берём имя из скобок как есть.
        var name = NamePattern().Match(powercfgOutput);
        if (name.Success && !string.IsNullOrWhiteSpace(name.Groups[1].Value))
        {
            return name.Groups[1].Value.Trim();
        }

        return guid.Success ? guid.Value : powercfgOutput.Trim();
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\(([^)]+)\)")]
    private static partial Regex NamePattern();
}
