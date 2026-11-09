using System.Text.RegularExpressions;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;

namespace WinOptimizer.Tweaks;

/// <summary>Переключает активную схему питания на «Высокая производительность».</summary>
public sealed partial class PowerPlanTweak : ITweak
{
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    private const string BackupName = "active-scheme";
    private static readonly string PowerCfg = Path.Combine(Environment.SystemDirectory, "powercfg.exe");

    private readonly BackupStore _backup;

    public PowerPlanTweak(TweakInfo info, BackupStore backup)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
    }

    public TweakInfo Info { get; }

    public bool RequiresRestorePoint => false;

    public bool IsApplied() =>
        string.Equals(GetActiveSchemeGuid(), HighPerformanceGuid, StringComparison.OrdinalIgnoreCase);

    public void Apply()
    {
        var current = GetActiveSchemeGuid();
        if (string.Equals(current, HighPerformanceGuid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _backup.Save(Info.Id, new[] { new SavedValue("powercfg", BackupName, "String", current, Existed: true) });
        SetActive(HighPerformanceGuid);
        Logger.Info($"Схема питания переключена на Высокую производительность (была {current})");
    }

    public void Revert()
    {
        var saved = _backup.Get(Info.Id)?.FirstOrDefault(v => v.Name == BackupName)?.Data;

        // Сохранённая схема могла быть удалена пользователем: тогда возвращаем «Сбалансированную».
        if (!(IsGuid(saved) && TrySetActive(saved!)))
        {
            SetActive(BalancedGuid);
        }

        _backup.Remove(Info.Id);
        Logger.Info("Схема питания возвращена");
    }

    private static string GetActiveSchemeGuid()
    {
        var result = ProcessRunner.Run(PowerCfg, "/getactivescheme", outputEncoding: ProcessRunner.OemEncoding);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"powercfg /getactivescheme завершился с кодом {result.ExitCode}: {result.StandardError.Trim()}");
        }

        var match = GuidPattern().Match(result.StandardOutput);
        return match.Success
            ? match.Value.ToLowerInvariant()
            : throw new InvalidOperationException("Не удалось определить активную схему питания.");
    }

    private static void SetActive(string guid)
    {
        if (!IsGuid(guid))
        {
            throw new ArgumentException("Некорректный идентификатор схемы питания.", nameof(guid));
        }

        var result = ProcessRunner.Run(PowerCfg, $"/setactive {guid}", outputEncoding: ProcessRunner.OemEncoding);
        if (result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException(
                $"Не удалось включить схему питания (код {result.ExitCode}). " +
                $"На ноутбуках с Modern Standby схема «Высокая производительность» может отсутствовать. {details.Trim()}");
        }
    }

    private static bool TrySetActive(string guid)
    {
        try
        {
            SetActive(guid);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Logger.Error($"Схема {guid} недоступна", ex);
            return false;
        }
    }

    private static bool IsGuid(string? value) => value is not null && GuidPattern().Match(value) is { Success: true } m && m.Length == value.Length;

    [GeneratedRegex(@"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
