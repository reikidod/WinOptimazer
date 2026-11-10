using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WinOptimizer.Services;

/// <summary>Где зарегистрирована программа автозагрузки (ключи Run в реестре).</summary>
public enum StartupScope
{
    CurrentUser,
    AllUsers,
    AllUsers32,
    CurrentUserFolder,
    AllUsersFolder,
}

public sealed record StartupEntry(StartupScope Scope, string Name, string Command, bool Enabled, string? ApprovalName = null)
{
    public string ScopeTitle => Scope switch
    {
        StartupScope.CurrentUser => "Реестр · только для вас",
        StartupScope.AllUsers => "Реестр · все пользователи",
        StartupScope.AllUsers32 => "Реестр · все (32-бит)",
        StartupScope.CurrentUserFolder => "Папка автозагрузки · только для вас",
        _ => "Папка автозагрузки · все пользователи",
    };
}

/// <summary>Доступ к реестру автозагрузки. Вынесен в интерфейс, чтобы логику можно было проверять без Windows.</summary>
public interface IStartupStore
{
    IReadOnlyDictionary<string, string> ReadRunValues(StartupScope scope);

    IReadOnlyDictionary<string, byte[]> ReadApproved(StartupScope scope);

    IReadOnlyList<string> ReadStartupFolder(StartupScope scope);

    void WriteApproved(StartupScope scope, string name, byte[] data);
}

public interface IStartupService
{
    IReadOnlyList<StartupEntry> GetEntries();

    void SetEnabled(StartupEntry entry, bool enabled);
}

/// <summary>
/// Формат значений StartupApproved\Run, которым пользуется диспетчер задач: 12 байт,
/// младший бит первого байта — «отключено» (02 — включено, 03 — отключено), в байтах 4–11 время отключения (FILETIME).
/// Формат не документирован Microsoft: он восстановлен по поведению диспетчера задач.
/// </summary>
public static class StartupApproval
{
    public static bool IsEnabled(byte[]? data) => data is null || data.Length == 0 || (data[0] & 1) == 0;

    public static byte[] Create(bool enabled, DateTime utcNow)
    {
        var data = new byte[12];
        if (enabled)
        {
            data[0] = 0x02;
        }
        else
        {
            data[0] = 0x03;
            BitConverter.TryWriteBytes(data.AsSpan(4), utcNow.ToFileTimeUtc());
        }

        return data;
    }
}

/// <summary>
/// Менеджер автозагрузки: показывает записи Run и файлы из пользовательской/общей папок Startup.
/// Состояние переключается через StartupApproved — исходные записи и файлы не удаляются.
/// Планировщик заданий намеренно не изменяется.
/// </summary>
public sealed class StartupService : IStartupService
{
    private static readonly StartupScope[] RegistryScopes = [StartupScope.CurrentUser, StartupScope.AllUsers, StartupScope.AllUsers32];
    private static readonly StartupScope[] FolderScopes = [StartupScope.CurrentUserFolder, StartupScope.AllUsersFolder];

    private readonly IStartupStore _store;
    private readonly Func<DateTime> _utcNow;

    public StartupService(IStartupStore store, Func<DateTime>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public IReadOnlyList<StartupEntry> GetEntries()
    {
        var result = new List<StartupEntry>();
        foreach (var scope in RegistryScopes)
        {
            var approved = _store.ReadApproved(scope);
            foreach (var (name, command) in _store.ReadRunValues(scope).OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                approved.TryGetValue(name, out var data);
                result.Add(new StartupEntry(scope, name, command, StartupApproval.IsEnabled(data), name));
            }
        }

        foreach (var scope in FolderScopes)
        {
            var approved = _store.ReadApproved(scope);
            foreach (var path in _store.ReadStartupFolder(scope).OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
            {
                var approvalName = approved.Keys.FirstOrDefault(key =>
                    string.Equals(key, path, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)) ?? path;
                approved.TryGetValue(approvalName, out var data);
                result.Add(new StartupEntry(scope, Path.GetFileNameWithoutExtension(path), path,
                    StartupApproval.IsEnabled(data), approvalName));
            }
        }

        return result;
    }

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _store.WriteApproved(entry.Scope, entry.ApprovalName ?? entry.Name, StartupApproval.Create(enabled, _utcNow()));
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupStore : IStartupStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static (RegistryHive Hive, string Run, string Approved) Map(StartupScope scope) => scope switch
    {
        StartupScope.CurrentUser => (RegistryHive.CurrentUser, RunKey, ApprovedRoot + "Run"),
        StartupScope.AllUsers => (RegistryHive.LocalMachine, RunKey, ApprovedRoot + "Run"),
        StartupScope.AllUsers32 => (RegistryHive.LocalMachine, Run32Key, ApprovedRoot + "Run32"),
        StartupScope.CurrentUserFolder => (RegistryHive.CurrentUser, string.Empty, ApprovedRoot + "StartupFolder"),
        StartupScope.AllUsersFolder => (RegistryHive.LocalMachine, string.Empty, ApprovedRoot + "StartupFolder"),
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    public IReadOnlyDictionary<string, string> ReadRunValues(StartupScope scope)
    {
        var (hive, runKey, _) = Map(scope);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(runKey, writable: false);
        if (key is null)
        {
            return result;
        }

        foreach (var name in key.GetValueNames())
        {
            // Имя нужно непустое, значение — строка: другие типы Windows в Run не запускает.
            if (name.Length > 0 && key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command)
            {
                result[name] = command;
            }
        }

        return result;
    }

    public IReadOnlyDictionary<string, byte[]> ReadApproved(StartupScope scope)
    {
        var (hive, _, approvedKey) = Map(scope);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(approvedKey, writable: false);
        if (key is null)
        {
            return result;
        }

        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is byte[] data)
            {
                result[name] = data;
            }
        }

        return result;
    }

    public IReadOnlyList<string> ReadStartupFolder(StartupScope scope)
    {
        var folder = scope switch
        {
            StartupScope.CurrentUserFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            StartupScope.AllUsersFolder => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), "Для этого источника папка автозагрузки не используется."),
        };

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];

        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void WriteApproved(StartupScope scope, string name, byte[] data)
    {
        var (hive, _, approvedKey) = Map(scope);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(approvedKey, writable: true)
                        ?? throw new InvalidOperationException($"Не удалось открыть раздел автозагрузки на запись ({scope}).");
        key.SetValue(name, data, RegistryValueKind.Binary);
    }
}
