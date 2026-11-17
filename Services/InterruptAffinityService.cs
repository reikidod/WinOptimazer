using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace WinOptimizer.Services;

public sealed record InterruptAffinityDevice(
    string DeviceId, string Name, string Category, int Processor,
    string Recommendation, string CurrentPolicy);

public sealed record InterruptAffinityResult(bool Success, string Message);

/// <summary>
/// Reversible interrupt-affinity configuration. Windows reads these values when the
/// device driver starts; changes are not advertised as immediately effective.
/// </summary>
public sealed class InterruptAffinityService
{
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\";
    private const string AffinitySuffix = @"Device Parameters\Interrupt Management\Affinity Policy";
    private const int SpecifiedProcessorsPolicy = 4;
    private const int MaximumSupportedProcessorsPerGroup = 64;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _backupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinOptimizer", "Backups", "interrupt-affinity-backup.json");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

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
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Поддерживается только Windows.");

        var (groupCount, processorCount) = GetSupportedTopology();
        if (processorCount < 2) return Array.Empty<InterruptAffinityDevice>();

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

            var path = EnumRoot + id + "\\" + AffinitySuffix;
            string policy = "По умолчанию Windows";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key?.GetValue("DevicePolicy") is int value)
                    policy = value switch
                    {
                        4 => "Выбранные процессоры",
                        2 => "Один близкий процессор",
                        5 => "Распределение MSI-X",
                        _ => $"Политика {value}"
                    };
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                policy = "Нет доступа к настройке";
            }

            // CPU 0 is intentionally excluded as a conservative starting point.
            // This is not P-core/E-core aware and does not claim a latency improvement.
            var processor = 1 + results.Count % (processorCount - 1);
            results.Add(new InterruptAffinityDevice(id, name, category, processor,
                $"Логический процессор {processor} · группа 0 · предварительная рекомендация", policy));
        }

        return results.OrderBy(x => x.Category).ThenBy(x => x.Name).ToArray();
    }

    internal static byte[] CreateProcessorMask(int processor, int processorCount)
    {
        if (processorCount is < 2 or > MaximumSupportedProcessorsPerGroup)
            throw new ArgumentOutOfRangeException(nameof(processorCount), "Поддерживается от 2 до 64 процессоров в одной группе.");
        if (processor < 1 || processor >= processorCount)
            throw new ArgumentOutOfRangeException(nameof(processor), "Номер процессора должен находиться в диапазоне 1..N-1.");

        var mask = new byte[sizeof(ulong)];
        mask[processor / 8] = (byte)(1 << (processor % 8));
        return mask;
    }

    private static (ushort GroupCount, int ProcessorCount) GetSupportedTopology()
    {
        try
        {
            var groups = GetActiveProcessorGroupCount();
            if (groups != 1)
                throw new NotSupportedException("Системы с несколькими группами процессоров пока не поддерживаются. Настройки не изменены.");

            var count = GetActiveProcessorCount(0);
            if (count is < 2 or > MaximumSupportedProcessorsPerGroup)
                throw new NotSupportedException("Не удалось безопасно определить топологию процессоров (поддерживается 2–64 логических процессора в одной группе).");

            return (groups, checked((int)count));
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException("Windows API для определения групп процессоров недоступен.", ex);
        }
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
        if (!OperatingSystem.IsWindows())
            return new(false, "Поддерживается только Windows.");
        if (!IsAdministrator)
            return new(false, "Для изменения системной политики прерываний запустите WinOptimizer от имени администратора.");
        if (devices.Count == 0)
            return new(false, "Сначала выполните сканирование устройств.");

        var backups = new List<RegistryBackup>();
        var backupPersisted = false;
        try
        {
            var (_, processorCount) = GetSupportedTopology();
            if (File.Exists(_backupPath))
                return new(false, "Уже существует резервная копия. Сначала выполните откат, чтобы не потерять исходные настройки.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in devices)
            {
                if (device is null || string.IsNullOrWhiteSpace(device.DeviceId) ||
                    device.DeviceId.Contains("..", StringComparison.Ordinal) ||
                    device.DeviceId.Contains('/') || device.DeviceId.Contains('\0') ||
                    !seen.Add(device.DeviceId))
                    throw new InvalidDataException("Список устройств содержит некорректный или повторяющийся идентификатор.");

                _ = CreateProcessorMask(device.Processor, processorCount);
                var deviceRoot = EnumRoot + device.DeviceId;
                using var parent = Registry.LocalMachine.OpenSubKey(deviceRoot);
                if (parent is null)
                    throw new InvalidDataException($"Устройство {device.Name} больше не существует. Выполните новое сканирование.");

                var keyPath = deviceRoot + "\\" + AffinitySuffix;
                using var existing = Registry.LocalMachine.OpenSubKey(keyPath);
                var oldPolicy = existing?.GetValue("DevicePolicy");
                var oldMask = existing?.GetValue("AssignmentSetOverride");
                if (oldPolicy is not null && oldPolicy is not int)
                    throw new InvalidDataException($"Неизвестный формат DevicePolicy для {device.Name}; изменения отменены.");

                var maskKind = oldMask is null ? null : existing!.GetValueKind("AssignmentSetOverride");
                var maskBytes = oldMask as byte[];
                int? maskDword = oldMask is int dword ? dword : null;
                long? maskQword = oldMask is long qword ? qword : null;
                if (oldMask is not null && maskBytes is null && maskDword is null && maskQword is null)
                    throw new InvalidDataException($"Неподдерживаемый формат AssignmentSetOverride для {device.Name}; изменения отменены.");
                if (maskKind is not null && maskKind is not (RegistryValueKind.Binary or RegistryValueKind.DWord or RegistryValueKind.QWord))
                    throw new InvalidDataException($"Неподдерживаемый тип AssignmentSetOverride для {device.Name}; изменения отменены.");

                backups.Add(new RegistryBackup(keyPath,
                    oldPolicy is not null, oldPolicy is int p ? p : 0,
                    maskKind?.ToString(),
                    maskBytes is null ? null : Convert.ToBase64String(maskBytes),
                    maskDword, maskQword));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
            var tempPath = _backupPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(backups, JsonOptions));
                File.Move(tempPath, _backupPath);
                backupPersisted = true;
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }

            foreach (var device in devices)
            {
                var keyPath = EnumRoot + device.DeviceId + "\\" + AffinitySuffix;
                var mask = CreateProcessorMask(device.Processor, processorCount);
                using var target = Registry.LocalMachine.CreateSubKey(keyPath, writable: true)
                    ?? throw new UnauthorizedAccessException($"Не удалось открыть параметры {device.Name}.");
                target.SetValue("DevicePolicy", SpecifiedProcessorsPolicy, RegistryValueKind.DWord);
                target.SetValue("AssignmentSetOverride", mask, RegistryValueKind.Binary);
            }

            return new(true, $"Политики подготовлены для устройств: {devices.Count}. Это экспериментальная настройка без гарантии снижения задержек. Перезагрузите Windows, чтобы драйверы перечитали параметры.");
        }
        catch (Exception ex)
        {
            if (backupPersisted)
            {
                try
                {
                    RestoreEntries(backups);
                    File.Delete(_backupPath);
                }
                catch
                {
                    // Preserve the complete backup so the user can retry the rollback.
                }
            }
            return new(false, $"Не удалось применить настройки. Изменения не считаются успешными. Причина: {ex.Message}");
        }
    }

    public InterruptAffinityResult Restore()
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "Поддерживается только Windows.");
        if (!IsAdministrator)
            return new(false, "Для отката запустите WinOptimizer от имени администратора.");
        if (!File.Exists(_backupPath))
            return new(false, "Резервная копия настроек не найдена.");

        try
        {
            var entries = JsonSerializer.Deserialize<List<RegistryBackup>>(File.ReadAllText(_backupPath))
                ?? throw new InvalidDataException("Резервная копия пуста или повреждена.");
            RestoreEntries(entries);
            File.Delete(_backupPath);
            return new(true, "Исходные политики прерываний восстановлены. Перезагрузите Windows для применения драйверами.");
        }
        catch (Exception ex)
        {
            return new(false, $"Не удалось выполнить полный откат: {ex.Message}. Резервная копия сохранена.");
        }
    }

    public bool HasBackup => File.Exists(_backupPath);

    private static void RestoreEntries(IEnumerable<RegistryBackup> entries)
    {
        foreach (var entry in entries.Reverse())
        {
            if (!entry.KeyPath.StartsWith(EnumRoot, StringComparison.OrdinalIgnoreCase) ||
                !entry.KeyPath.EndsWith("\\" + AffinitySuffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Резервная копия содержит недопустимый путь реестра.");

            using var key = Registry.LocalMachine.OpenSubKey(entry.KeyPath, writable: true);
            if (key is null) continue; // The device was removed; there is no live setting to restore.

            if (entry.HadPolicy) key.SetValue("DevicePolicy", entry.Policy, RegistryValueKind.DWord);
            else key.DeleteValue("DevicePolicy", throwOnMissingValue: false);

            if (entry.MaskKind is null)
            {
                key.DeleteValue("AssignmentSetOverride", throwOnMissingValue: false);
            }
            else
            {
                var kind = Enum.Parse<RegistryValueKind>(entry.MaskKind);
                switch (kind)
                {
                    case RegistryValueKind.Binary:
                        key.SetValue("AssignmentSetOverride",
                            Convert.FromBase64String(entry.MaskBase64 ?? throw new InvalidDataException("Повреждена резервная копия маски.")),
                            RegistryValueKind.Binary);
                        break;
                    case RegistryValueKind.DWord:
                        key.SetValue("AssignmentSetOverride",
                            entry.MaskDword ?? throw new InvalidDataException("Повреждена DWORD-маска."), RegistryValueKind.DWord);
                        break;
                    case RegistryValueKind.QWord:
                        key.SetValue("AssignmentSetOverride",
                            entry.MaskQword ?? throw new InvalidDataException("Повреждена QWORD-маска."), RegistryValueKind.QWord);
                        break;
                    default:
                        throw new InvalidDataException("Неизвестный тип резервной маски.");
                }
            }
        }
    }

    private sealed record RegistryBackup(
        string KeyPath, bool HadPolicy, int Policy, string? MaskKind,
        string? MaskBase64, int? MaskDword, long? MaskQword);
}
