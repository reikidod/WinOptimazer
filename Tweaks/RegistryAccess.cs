using System.Globalization;
using Microsoft.Win32;

namespace WinOptimizer.Tweaks;

public readonly record struct RegistryValue(object Value, RegistryValueKind Kind);

/// <summary>Одно значение реестра, которое меняет твик.</summary>
/// <param name="DefaultValue">Значение Windows по умолчанию; null означает «значения по умолчанию нет — удалить».</param>
public sealed record RegistryEntry(
    RegistryHive Hive,
    string SubKey,
    string Name,
    RegistryValueKind Kind,
    object TweakValue,
    object? DefaultValue = null)
{
    public string FullKey => $"{HiveName}\\{SubKey}";

    private string HiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        _ => Hive.ToString(),
    };
}

/// <summary>Тонкая обёртка над реестром: всегда 64-битное представление.</summary>
internal static class RegistryAccess
{
    public static RegistryValue? Read(RegistryEntry entry)
    {
        using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(entry.SubKey, writable: false);
        if (key is null)
        {
            return null;
        }

        var value = key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : new RegistryValue(value, key.GetValueKind(entry.Name));
    }

    public static void Write(RegistryEntry entry, RegistryValueKind kind, object value)
    {
        using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(entry.SubKey, writable: true)
                        ?? throw new InvalidOperationException($"Не удалось открыть раздел {entry.FullKey} на запись.");
        key.SetValue(entry.Name, value, kind);
    }

    public static void Delete(RegistryEntry entry)
    {
        using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(entry.SubKey, writable: true);
        key?.DeleteValue(entry.Name, throwOnMissingValue: false);
    }

    /// <summary>Совпадает ли текущее значение со значением твика (и тип, и данные).</summary>
    public static bool Matches(RegistryValue? actual, RegistryEntry entry)
    {
        if (actual is not { } value || value.Kind != entry.Kind)
        {
            return false;
        }

        return entry.Kind switch
        {
            RegistryValueKind.DWord => value.Value is int actualInt
                                       && actualInt == Convert.ToInt32(entry.TweakValue, CultureInfo.InvariantCulture),
            RegistryValueKind.String => value.Value is string actualText
                                        && string.Equals(actualText, (string)entry.TweakValue, StringComparison.Ordinal),
            _ => throw new NotSupportedException($"Тип значения {entry.Kind} не поддерживается."),
        };
    }

    public static string Serialize(RegistryValue value) => value.Kind switch
    {
        RegistryValueKind.DWord => ((int)value.Value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => ((long)value.Value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.String or RegistryValueKind.ExpandString => (string)value.Value,
        _ => throw new NotSupportedException($"Тип значения {value.Kind} не поддерживается для бэкапа."),
    };

    public static object Deserialize(string data, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(data, NumberStyles.Integer, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(data, NumberStyles.Integer, CultureInfo.InvariantCulture),
        RegistryValueKind.String or RegistryValueKind.ExpandString => data,
        _ => throw new NotSupportedException($"Тип значения {kind} не поддерживается для бэкапа."),
    };
}
