using System.Text.RegularExpressions;

namespace WinOptimizer.Services;

/// <summary>Читаемое название схемы питания из вывода «powercfg /getactivescheme».</summary>
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
