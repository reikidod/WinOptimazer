using System.Management;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace WinOptimizer.Services;

public sealed record InterruptAffinityDevice(
    string DeviceId, string Name, string Category, int Processor,
    string Recommendation, string CurrentPolicy);

public sealed record InterruptAffinityResult(bool Success, string Message);

/// <summary>
/// Conservative, reversible interrupt-affinity configuration. Windows reads these values
/// when the device driver starts; changes are not advertised as immediately effective.
/// </summary>
public sealed class InterruptAffinityService
{
    private const string AffinitySuffix = @"Device Parameters\Interrupt Management\Affinity Policy";
    private const int SpecifiedProcessorsPolicy = 4;
    private const int MaximumSupportedProcessorsPerGroup = 64;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _backupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinOptimizer", "Backups", "interrupt-affinity-backup.json");

    public bool IsAdministrator
    {
        get
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    public IReadOnlyList<InterruptAffinityDevice> Scan()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Поддерживается только Windows.");
        var logical = Environment.ProcessorCount;
        if (logical < 2) return Array.Empty<InterruptAffinityDevice>();
        var count = Math.Min(logical, MaximumSupportedProcessorsPerGroup);
        var results = new List<InterruptAffinityDevice>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID, PNPClass FROM Win32_PnPEntity WHERE PNPDeviceID IS NOT NULL");
        using var collection = searcher.Get();
        foreach (ManagementObject item in collection)
        {
            var id = item["PNPDeviceID"] as string;
            var name = item["Name"] as string;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            var category = Categorize(name, item["PNPClass"] as string);
            if (category is null) continue;
            var path = $@"SYSTEM\CurrentControlSet\Enum\{id}\{AffinitySuffix}";
            string policy = "По умолчанию Windows";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key?.GetValue("DevicePolicy") is int value)
                    policy = value switch { 4 => "Выбранные процессоры", 2 => "Один близкий процессор", 5 => "Распределение MSI-X", _ => $"Политика {value}" };
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { policy = "Нет доступа к настройке"; }

            // CPU 0 is left for Windows/system activity. The recommendation is a starting point,
            // not a claim that this logical processor is a P-core or physically isolated.
            var processor = 1 + results.Count % (count - 1);
            results.Add(new InterruptAffinityDevice(id, name, category, processor,
                $"Логический процессор {processor} · группа 0", policy));
        }
        return results.OrderBy(x => x.Category).ThenBy(x => x.Name).ToArray();
    }

    private static string? Categorize(string name, string? pnpClass)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("display") || n.Contains("nvidia") || n.Contains("radeon") || n.Contains("geforce") || pnpClass == "Display") return "Графика";
        if (n.Contains("ethernet") || n.Contains("wi-fi") || n.Contains("wireless") || n.Contains("network") || pnpClass == "Net") return "Сеть";
        if (n.Contains("usb") || pnpClass == "USB") return "USB";
        if (n.Contains("nvme") || n.Contains("storage") || n.Contains("raid") || n.Contains("standard nvm") || pnpClass == "SCSIAdapter") return "Накопители";
        if (n.Contains("audio") || n.Contains("sound") || pnpClass == "MEDIA") return "Аудио";
        return null;
    }

    public InterruptAffinityResult Apply(IReadOnlyList<InterruptAffinityDevice> devices)
    {
        if (!IsAdministrator) return new(false, "Для изменения системной политики прерываний запустите WinOptimizer от имени администратора.");
        if (devices.Count == 0) return new(false, "Сначала выполните сканирование устройств.");
        if (File.Exists(_backupPath)) return new(false, "Уже существует резервная копия. Сначала выполните откат, чтобы не потерять исходные настройки.");
        var backups = new List<RegistryBackup>();
        try
        {
            foreach (var device in devices)
            {
                if (device.Processor is < 1 or >= MaximumSupportedProcessorsPerGroup)
                    throw new InvalidDataException($"Недопустимый номер процессора для {device.Name}.");
                var keyPath = $@"SYSTEM\CurrentControlSet\Enum\{device.DeviceId}\{AffinitySuffix}";
                using var existing = Registry.LocalMachine.OpenSubKey(keyPath);
                var oldPolicy = existing?.GetValue("DevicePolicy");
                var oldMask = existing?.GetValue("AssignmentSetOverride");
                backups.Add(new RegistryBackup(keyPath,
                    oldPolicy is not null, oldPolicy is int p ? p : 0,
                    oldMask is byte[] bytes ? Convert.ToBase64String(bytes) : null,
                    existing?.GetValueKind("AssignmentSetOverride").ToString()));

                var mask = new byte[8];
                mask[device.Processor / 8] = (byte)(1 << (device.Processor % 8));
                using var target = Registry.LocalMachine.CreateSubKey(keyPath, writable: true)
                    ?? throw new UnauthorizedAccessException($"Не удалось открыть параметры {device.Name}.");
                target.SetValue("DevicePolicy", SpecifiedProcessorsPolicy, RegistryValueKind.DWord);
                target.SetValue("AssignmentSetOverride", mask, RegistryValueKind.Binary);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
            File.WriteAllText(_backupPath, JsonSerializer.Serialize(backups, JsonOptions));
            return new(true, $"Политики подготовлены для устройств: {devices.Count}. Перезагрузите Windows, чтобы драйверы перечитали параметры.");
        }
        catch (Exception ex)
        {
            try { RestoreEntries(backups); } catch { /* original error remains primary */ }
            return new(false, $"Не удалось применить настройки. Выполнена попытка вернуть уже изменённые значения. Причина: {ex.Message}");
        }
    }

    public InterruptAffinityResult Restore()
    {
        if (!IsAdministrator) return new(false, "Для отката запустите WinOptimizer от имени администратора.");
        if (!File.Exists(_backupPath)) return new(false, "Резервная копия настроек не найдена.");
        try
        {
            var entries = JsonSerializer.Deserialize<List<RegistryBackup>>(File.ReadAllText(_backupPath))
                ?? throw new InvalidDataException("Резервная копия пуста или повреждена.");
            RestoreEntries(entries);
            File.Delete(_backupPath);
            return new(true, "Исходные политики прерываний восстановлены. Перезагрузите Windows для применения драйверами.");
        }
        catch (Exception ex) { return new(false, $"Не удалось выполнить полный откат: {ex.Message}. Резервная копия сохранена."); }
    }

    public bool HasBackup => File.Exists(_backupPath);

    private static void RestoreEntries(IEnumerable<RegistryBackup> entries)
    {
        foreach (var entry in entries.Reverse())
        {
            using var key = Registry.LocalMachine.CreateSubKey(entry.KeyPath, writable: true)
                ?? throw new UnauthorizedAccessException($"Не удалось восстановить {entry.KeyPath}.");
            if (entry.HadPolicy) key.SetValue("DevicePolicy", entry.Policy, RegistryValueKind.DWord);
            else key.DeleteValue("DevicePolicy", throwOnMissingValue: false);
            if (entry.MaskBase64 is not null)
                key.SetValue("AssignmentSetOverride", Convert.FromBase64String(entry.MaskBase64), RegistryValueKind.Binary);
            else
                key.DeleteValue("AssignmentSetOverride", throwOnMissingValue: false);
        }
    }

    private sealed record RegistryBackup(string KeyPath, bool HadPolicy, int Policy, string? MaskBase64, string? MaskKind);
}
