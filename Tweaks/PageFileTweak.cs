using System.Security;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Tweaks;

/// <summary>
/// Файл подкачки фиксированного размера на системном диске. Windows хранит настройку в значении
/// Memory Management\PagingFiles (REG_MULTI_SZ); «?:\pagefile.sys» означает «размер выбирает система».
/// Исходное значение сохраняется в бэкапе, откат возвращает его (или автоматический режим).
/// </summary>
public sealed class PageFileTweak : ITweak
{
    internal const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    internal const string PagingFilesValue = "PagingFiles";
    internal const string SystemManagedEntry = @"?:\pagefile.sys";

    private const int MinSizeMb = 4096;
    private const int MaxSizeMb = 8192;
    private const string Separator = "\n";

    private readonly BackupStore _backup;
    private readonly string _entry;

    public PageFileTweak(TweakInfo info, BackupStore backup, int sizeMb, string? drive = null)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        if (sizeMb < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeMb), "Размер файла подкачки не может быть меньше 16 МБ.");
        }

        _entry = BuildEntry(drive ?? SystemDrive(), sizeMb);
    }

    public TweakInfo Info { get; }

    public bool RequiresRestorePoint => true;

    /// <summary>Половина ОЗУ, но не меньше 4 ГБ и не больше 8 ГБ, кратно 256 МБ.</summary>
    public static int RecommendedSizeMb(long ramBytes)
    {
        var ramMb = Math.Max(0, ramBytes / (1024 * 1024));
        var half = (int)Math.Min(int.MaxValue, ramMb / 2);
        var clamped = Math.Clamp(half, MinSizeMb, MaxSizeMb);
        return clamped / 256 * 256;
    }

    /// <summary>Строка для PagingFiles: «C:\pagefile.sys 4096 4096» (начальный и максимальный размер, МБ).</summary>
    public static string BuildEntry(string drive, int sizeMb)
    {
        var letter = drive.TrimEnd('\\', '/').TrimEnd(':');
        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
        {
            throw new ArgumentException($"Некорректный диск: «{drive}».", nameof(drive));
        }

        return $"{char.ToUpperInvariant(letter[0])}:\\pagefile.sys {sizeMb} {sizeMb}";
    }

    public bool IsApplied() => IsSame(ReadPagingFiles(), _entry);

    public void Apply()
    {
        var current = ReadPagingFiles();
        if (IsSame(current, _entry))
        {
            return;
        }

        // Снимок до записи: если он не сохранился, система остаётся нетронутой.
        _backup.Save(Info.Id, new[]
        {
            new SavedValue(MemoryManagementKey, PagingFilesValue, nameof(RegistryValueKind.MultiString),
                current is null ? null : string.Join(Separator, current), Existed: current is not null),
        });

        WritePagingFiles([_entry]);
        Logger.Info($"Файл подкачки: {_entry} (было: {(current is null ? "нет значения" : string.Join("; ", current))})");
    }

    public void Revert()
    {
        var saved = _backup.Get(Info.Id)?.FirstOrDefault(v => v.Name == PagingFilesValue);
        string[] restore = saved is { Existed: true, Data: { } data }
            ? data.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
            : [SystemManagedEntry];

        WritePagingFiles(restore.Length > 0 ? restore : [SystemManagedEntry]);
        _backup.Remove(Info.Id);
        Logger.Info("Файл подкачки возвращён к прежним настройкам");
    }

    private static string SystemDrive() =>
        Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

    private static bool IsSame(string[]? current, string entry) =>
        current is { Length: 1 } && string.Equals(current[0].Trim(), entry, StringComparison.OrdinalIgnoreCase);

    private static string[]? ReadPagingFiles()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(MemoryManagementKey, writable: false);
        return key?.GetValue(PagingFilesValue) switch
        {
            string[] lines => lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray(),
            string single when !string.IsNullOrWhiteSpace(single) => [single],
            _ => null,
        };
    }

    private static void WritePagingFiles(string[] lines)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(MemoryManagementKey, writable: true)
                            ?? throw new InvalidOperationException($"Не удалось открыть HKLM\\{MemoryManagementKey} на запись.");
            key.SetValue(PagingFilesValue, lines, RegistryValueKind.MultiString);
        }
        catch (SecurityException ex)
        {
            throw new UnauthorizedAccessException("Нет прав на изменение настроек файла подкачки. Запустите приложение от имени администратора.", ex);
        }
    }
}
