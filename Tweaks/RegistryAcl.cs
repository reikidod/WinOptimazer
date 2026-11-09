using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Tweaks;

/// <summary>
/// Исправляет «Attempted to perform an unauthorized operation» на защищённых политических ключах
/// (например HKLM\SOFTWARE\Policies\Microsoft\Dsh в Windows 11): их владелец — TrustedInstaller/SYSTEM,
/// и даже администратор не может туда писать. Берём ключ во владение и даём группе «Администраторы» полный доступ.
/// </summary>
internal static class RegistryAcl
{
    private const string PoliciesPrefix = @"SOFTWARE\Policies\";
    private const string SeTakeOwnershipPrivilege = "SeTakeOwnershipPrivilege";
    private const string SeRestorePrivilege = "SeRestorePrivilege";
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    /// <summary>Чиним права только на ветке политик в HKLM — другие разделы системы не трогаем.</summary>
    public static bool CanRepair(RegistryEntry entry) =>
        entry.Hive == RegistryHive.LocalMachine
        && entry.SubKey.StartsWith(PoliciesPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Возвращает true, если права удалось выдать и запись можно повторить.</summary>
    public static bool TryGrantAdministrators(RegistryEntry entry)
    {
        try
        {
            // Привилегии выключены по умолчанию даже у администратора.
            EnablePrivilege(SeTakeOwnershipPrivilege);
            EnablePrivilege(SeRestorePrivilege);

            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);

            // Ключа может ещё не быть: тогда чиним ближайший существующий родитель (но не выше «Policies»).
            for (var path = entry.SubKey; path.Length > PoliciesPrefix.Length; path = Parent(path))
            {
                if (!TryTakeOwnership(baseKey, path, admins, out var exists))
                {
                    if (!exists)
                    {
                        continue;
                    }

                    return false;
                }

                GrantFullControl(baseKey, path, admins);
                Logger.Info($"Выданы права «Администраторы» на {entry.Hive}\\{path}");
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Logger.Error($"Не удалось выдать права на {entry.FullKey}", ex);
            return false;
        }
    }

    private static bool TryTakeOwnership(RegistryKey baseKey, string path, SecurityIdentifier owner, out bool exists)
    {
        using var key = baseKey.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership);
        exists = key is not null;
        if (key is null)
        {
            return false;
        }

        var security = new RegistrySecurity();
        security.SetOwner(owner);
        key.SetAccessControl(security);
        return true;
    }

    private static void GrantFullControl(RegistryKey baseKey, string path, SecurityIdentifier admins)
    {
        using var key = baseKey.OpenSubKey(
            path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)
            ?? throw new InvalidOperationException($"Раздел {path} исчез во время смены прав.");

        var security = key.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new RegistryAccessRule(
            admins, RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        key.SetAccessControl(security);
    }

    private static string Parent(string path)
    {
        var i = path.LastIndexOf('\\');
        return i < 0 ? string.Empty : path[..i];
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            var state = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)
                || Marshal.GetLastWin32Error() != 0)
            {
                // 1300 = ERROR_NOT_ALL_ASSIGNED: у процесса нет такой привилегии (запущен не от администратора).
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);
}
