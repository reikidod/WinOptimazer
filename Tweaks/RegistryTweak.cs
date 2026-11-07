using System.Security;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Tweaks;

/// <summary>
/// Твик, состоящий из набора значений реестра. Перед изменением снимает снимок исходных
/// значений, а при ошибке записи откатывает уже записанное.
/// </summary>
public sealed class RegistryTweak : ITweak
{
    private readonly IReadOnlyList<RegistryEntry> _entries;
    private readonly BackupStore _backup;
    private readonly Action? _afterChange;

    public RegistryTweak(TweakInfo info, IReadOnlyList<RegistryEntry> entries, BackupStore backup, Action? afterChange = null)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _entries = entries is { Count: > 0 } ? entries : throw new ArgumentException("Нужна хотя бы одна запись реестра.", nameof(entries));
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        _afterChange = afterChange;
    }

    public TweakInfo Info { get; }

    /// <summary>Любое изменение HKLM считается системным и требует предложения точки восстановления.</summary>
    public bool RequiresRestorePoint => _entries.Any(e => e.Hive == RegistryHive.LocalMachine);

    public bool IsApplied() => _entries.All(e => RegistryAccess.Matches(RegistryAccess.Read(e), e));

    public void Apply()
    {
        // Снимок снимается до любых изменений: если он не удался, система остаётся нетронутой.
        var snapshot = BuildSnapshot();
        _backup.Save(Info.Id, snapshot);

        var written = new List<RegistryEntry>();
        try
        {
            foreach (var entry in _entries)
            {
                RegistryAccess.Write(entry, entry.Kind, entry.TweakValue);
                written.Add(entry);
            }
        }
        catch
        {
            RollbackBestEffort(written, snapshot);
            throw;
        }

        RunAfterChange();
        Logger.Info($"Применён твик {Info.Id}");
    }

    public void Revert()
    {
        var saved = _backup.Get(Info.Id);
        foreach (var entry in _entries)
        {
            Restore(entry, FindSaved(saved, entry));
        }

        _backup.Remove(Info.Id);
        RunAfterChange();
        Logger.Info($"Откатан твик {Info.Id}");
    }

    private List<SavedValue> BuildSnapshot()
    {
        var previous = _backup.Get(Info.Id);
        var result = new List<SavedValue>();

        foreach (var entry in _entries)
        {
            var current = RegistryAccess.Read(entry);
            if (RegistryAccess.Matches(current, entry))
            {
                // Значение уже равно значению твика: «исходным» его считать нельзя,
                // сохраняем то, что было записано раньше (если было).
                var earlier = FindSaved(previous, entry);
                if (earlier is not null)
                {
                    result.Add(earlier);
                }

                continue;
            }

            result.Add(current is { } value
                ? new SavedValue(entry.FullKey, entry.Name, value.Kind.ToString(), RegistryAccess.Serialize(value), Existed: true)
                : new SavedValue(entry.FullKey, entry.Name, entry.Kind.ToString(), Data: null, Existed: false));
        }

        return result;
    }

    private static SavedValue? FindSaved(IReadOnlyList<SavedValue>? saved, RegistryEntry entry) =>
        saved?.FirstOrDefault(s => s.Key == entry.FullKey && s.Name == entry.Name);

    private static void Restore(RegistryEntry entry, SavedValue? saved)
    {
        if (saved is not null)
        {
            if (!saved.Existed)
            {
                RegistryAccess.Delete(entry);
                return;
            }

            var kind = Enum.Parse<RegistryValueKind>(saved.Kind);
            var data = saved.Data ?? throw new InvalidDataException($"В бэкапе нет данных для {entry.FullKey}\\{entry.Name}.");
            RegistryAccess.Write(entry, kind, RegistryAccess.Deserialize(data, kind));
            return;
        }

        if (entry.DefaultValue is null)
        {
            RegistryAccess.Delete(entry);
        }
        else
        {
            RegistryAccess.Write(entry, entry.Kind, entry.DefaultValue);
        }
    }

    private void RollbackBestEffort(IEnumerable<RegistryEntry> written, IReadOnlyList<SavedValue> snapshot)
    {
        foreach (var entry in written)
        {
            try
            {
                Restore(entry, FindSaved(snapshot, entry));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidDataException)
            {
                // Исходная ошибка важнее: фиксируем неудачный откат в логе и пробрасываем её дальше.
                Logger.Error($"Не удалось откатить {entry.FullKey}\\{entry.Name} после сбоя", ex);
            }
        }
    }

    private void RunAfterChange()
    {
        try
        {
            _afterChange?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Значения уже записаны, просто не применились «на лету»: достаточно перезахода.
            Logger.Error($"Твик {Info.Id}: значения записаны, но применить без перезахода не удалось", ex);
        }
    }
}
