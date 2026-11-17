using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace WinOptimizer.Services;

public sealed record InterruptAffinityDevice(
    string DeviceId, string Name, string Category, int Processor,
    string Recommendation, string CurrentPolicy, ulong ProcessorMask = 0,
    IReadOnlyList<InterruptAffinityCore>? AvailableCores = null, int ProcessorCount = 0);

public sealed record InterruptAffinityCore(int Index, IReadOnlyList<int> LogicalProcessors);

public sealed record InterruptAffinityResult(bool Success, string Message);

/// <summary>
/// Reversible interrupt-affinity configuration. Recommendations use physical-core
/// topology and one logical processor per core by default. Windows reads these values
/// when the device driver starts; changes are not advertised as immediately effective.
/// </summary>
public sealed class InterruptAffinityService
{
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\";
    private const string AffinitySuffix = @"Device Parameters\Interrupt Management\Affinity Policy";
    private const int SpecifiedProcessorsPolicy = 4;
    private const int MaximumSupportedProcessorsPerGroup = 64;
    private const int RelationProcessorCore = 0;
    private const int GroupAffinitySize = 16;
    private const int ProcessorCoreGroupMaskOffset = 32;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _backupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinOptimizer", "Backups", "interrupt-affinity-backup.json");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType, IntPtr buffer, ref uint returnedLength);

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

        var topology = GetSupportedTopology();
        if (topology.ProcessorCount < 2) return Array.Empty<InterruptAffinityDevice>();

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

            var selectedCores = GetRecommendedCoreIndexes(category, topology.Cores.Count);
            var selectedProcessors = selectedCores
                .Select(coreIndex => topology.Cores[coreIndex].LogicalProcessors[0])
                .Distinct()
                .ToArray();
            var mask = selectedProcessors.Aggregate(0UL, (current, cpu) => current | (1UL << cpu));
            var coreLabels = string.Join(" + ", selectedCores.Select(index => $"ядро {index}"));
            var cpuLabels = string.Join(", ", selectedProcessors.Select(cpu => $"CPU {cpu}"));
            var recommendation = selectedProcessors.Length > 1
                ? $"Физические {coreLabels} · потоки {cpuLabels} · несколько ядер"
                : $"Физическое {coreLabels} · поток {cpuLabels}";

            results.Add(new InterruptAffinityDevice(
                id, name, category, selectedProcessors[0], recommendation, policy, mask,
                topology.Cores.Select(core => new InterruptAffinityCore(core.Index, core.LogicalProcessors)).ToArray(),
                topology.ProcessorCount));
        }

        return results.OrderBy(x => x.Category).ThenBy(x => x.Name).ToArray();
    }

    internal static byte[] CreateProcessorMask(int processor, int processorCount) =>
        CreateProcessorMask(new[] { processor }, processorCount);

    internal static byte[] CreateProcessorMask(IReadOnlyCollection<int> processors, int processorCount)
    {
        if (processorCount is < 1 or > MaximumSupportedProcessorsPerGroup)
            throw new ArgumentOutOfRangeException(nameof(processorCount),
                "Поддерживается от 1 до 64 логических процессоров в одной группе.");
        if (processors is null || processors.Count == 0)
            throw new ArgumentException("Необходимо выбрать хотя бы один логический процессор.", nameof(processors));

        ulong mask = 0;
        foreach (var processor in processors)
        {
            if (processor < 0 || processor >= processorCount)
                throw new ArgumentOutOfRangeException(nameof(processors),
                    "Номер логического процессора находится вне диапазона доступной группы.");
            mask |= 1UL << processor;
        }

        return BitConverter.GetBytes(mask);
    }

    internal static ulong CreateCoreProcessorMask(
        IReadOnlyList<InterruptAffinityCore> cores,
        IReadOnlyCollection<int> selectedCoreIndexes,
        int processorCount)
    {
        if (cores is null || selectedCoreIndexes is null || selectedCoreIndexes.Count == 0)
            throw new ArgumentException("Выберите хотя бы одно физическое ядро.", nameof(selectedCoreIndexes));

        var selected = new HashSet<int>();
        var processors = new List<int>();
        foreach (var coreIndex in selectedCoreIndexes)
        {
            var core = cores.FirstOrDefault(item => item.Index == coreIndex)
                ?? throw new ArgumentOutOfRangeException(nameof(selectedCoreIndexes), "Выбрано неизвестное физическое ядро.");
            if (selected.Add(coreIndex))
                processors.AddRange(core.LogicalProcessors);
        }

        return BitConverter.ToUInt64(CreateProcessorMask(processors.Distinct().ToArray(), processorCount));
    }

    public static bool CanRestartDevice(InterruptAffinityDevice device)
    {
        if (device is null || string.IsNullOrWhiteSpace(device.DeviceId))
            return false;

        var isPci = device.DeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase);
        var isUsb = device.DeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
        if (device.Category is "Графика" or "Сеть")
            return isPci;
        if (device.Category == "USB-контроллеры")
            return isPci || isUsb;
        if (device.Category == "Аудио")
            return isUsb;

        // Storage controllers and system bridges remain blocked because restarting
        // them can interrupt OS/data access or destabilize the PCI device tree.
        return false;
    }

    public static bool RequiresRestartConfirmation(InterruptAffinityDevice device) =>
        device is not null &&
        (device.Category == "USB-контроллеры" ||
         device.DeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase));

    public InterruptAffinityResult RestartDevice(InterruptAffinityDevice device)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "Перезапуск устройств поддерживается только в Windows.");
        if (!IsAdministrator)
            return new(false, "Для перезапуска устройства запустите WinOptimizer от имени администратора.");
        if (device is null || !CanRestartDevice(device))
            return new(false, "Перезапуск этого устройства заблокирован: контроллеры дисков и системные мосты остаются защищёнными.");
        if (Environment.OSVersion.Version.Build < 19041)
            return new(false, "Команда pnputil /restart-device требует Windows 10 версии 2004 или новее.");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("/restart-device");
            process.StartInfo.ArgumentList.Add(device.DeviceId);
            if (!process.Start())
                return new(false, "Не удалось запустить PnPUtil.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                return new(false, "Перезапуск устройства превысил лимит 30 секунд. Проверьте состояние устройства.");
            }

            Task.WaitAll(stdoutTask, stderrTask);
            var output = string.Join(" ", new[] { stdoutTask.Result, stderrTask.Result }
                .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            return process.ExitCode == 0
                ? new(true, $"Команда перезапуска отправлена для «{device.Name}». Проверьте состояние устройства и повторно измерьте DPC/ISR. {output}".Trim())
                : new(false, $"Windows не смогла перезапустить «{device.Name}» (код {process.ExitCode}). {output}".Trim());
        }
        catch (Exception ex)
        {
            return new(false, $"Ошибка перезапуска «{device.Name}»: {ex.Message}");
        }
    }

    private static int[] GetRecommendedCoreIndexes(string category, int coreCount)
    {
        if (coreCount <= 0) throw new InvalidOperationException("Не удалось определить физические ядра.");

        // Spread interrupt sources across physical cores. The selected logical CPU
        // for each core is obtained from Windows topology; sibling numbering is never assumed.
        var preferred = category switch
        {
            "Графика" => new[] { 0 },
            "USB-контроллеры" => new[] { 1 },
            "Сеть" => new[] { 2, 3 },
            "Накопители" => new[] { 4 },
            "Аудио" => new[] { 5 },
            _ => new[] { 1 + (StringComparer.Ordinal.GetHashCode(category) & int.MaxValue) % Math.Max(1, coreCount - 1) }
        };

        return preferred.Select(index => index % coreCount).Distinct().ToArray();
    }

    private static ProcessorTopology GetSupportedTopology()
    {
        try
        {
            var groups = GetActiveProcessorGroupCount();
            if (groups != 1)
                throw new NotSupportedException(
                    "Системы с несколькими группами процессоров пока не поддерживаются. Настройки не изменены.");

            var count = checked((int)GetActiveProcessorCount(0));
            if (count is < 1 or > MaximumSupportedProcessorsPerGroup)
                throw new NotSupportedException(
                    "Не удалось безопасно определить число логических процессоров (поддерживается 1–64 в группе).");

            var cores = ReadPhysicalCores();
            var discoveredCount = cores.Sum(core => core.LogicalProcessors.Count);
            if (cores.Count == 0 || discoveredCount != count)
                throw new InvalidOperationException(
                    "Windows вернула неполную топологию физических ядер. Настройки не изменены.");

            return new ProcessorTopology(count, cores);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException("Windows API для определения топологии процессоров недоступен.", ex);
        }
    }

    private static IReadOnlyList<ProcessorCore> ReadPhysicalCores()
    {
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        var error = Marshal.GetLastWin32Error();
        if (length == 0 || (error != 122 && error != 0))
            throw new Win32Exception(error, "Не удалось запросить топологию физических ядер.");

        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Не удалось получить топологию физических ядер.");

            var cores = new List<ProcessorCore>();
            var offset = 0;
            var totalLength = checked((int)length);
            while (offset + 8 <= totalLength)
            {
                var relationship = Marshal.ReadInt32(buffer, offset);
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || offset + size > totalLength)
                    throw new InvalidDataException("Windows вернула повреждённую структуру топологии процессора.");

                if (relationship == RelationProcessorCore)
                {
                    if (size < ProcessorCoreGroupMaskOffset)
                        throw new InvalidDataException("Структура физического ядра имеет неверный размер.");

                    var groupCount = unchecked((ushort)Marshal.ReadInt16(buffer, offset + 30));
                    if (groupCount == 0 ||
                        ProcessorCoreGroupMaskOffset + groupCount * GroupAffinitySize > size)
                        throw new InvalidDataException("Не удалось прочитать маски логических процессоров ядра.");

                    var logicalProcessors = new List<int>();
                    for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
                    {
                        var affinityOffset = offset + ProcessorCoreGroupMaskOffset + groupIndex * GroupAffinitySize;
                        var group = unchecked((ushort)Marshal.ReadInt16(buffer, affinityOffset + 8));
                        if (group != 0)
                            throw new NotSupportedException(
                                "Топология физических ядер содержит несколько групп процессоров; настройки не изменены.");

                        var affinityMask = unchecked((ulong)Marshal.ReadInt64(buffer, affinityOffset));
                        for (var bit = 0; bit < 64; bit++)
                            if ((affinityMask & (1UL << bit)) != 0)
                                logicalProcessors.Add(bit);
                    }

                    if (logicalProcessors.Count == 0)
                        throw new InvalidDataException("Для физического ядра не найдены логические процессоры.");
                    cores.Add(new ProcessorCore(cores.Count, logicalProcessors.Order().ToArray()));
                }

                offset += size;
            }

            if (offset != totalLength)
                throw new InvalidDataException("Размер данных топологии не совпадает с размером структур Windows.");

            return cores;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? Categorize(string name, string? pnpClass)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("display") || n.Contains("nvidia") || n.Contains("radeon") ||
            n.Contains("geforce") || pnpClass == "Display") return "Графика";
        if (n.Contains("ethernet") || n.Contains("wi-fi") || n.Contains("wireless") ||
            n.Contains("network") || pnpClass == "Net") return "Сеть";
        if (n.Contains("usb") || pnpClass == "USB") return "USB-контроллеры";
        if (n.Contains("nvme") || n.Contains("storage") || n.Contains("raid") ||
            n.Contains("standard nvm") || pnpClass == "SCSIAdapter") return "Накопители";
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
            var topology = GetSupportedTopology();
            var processorCount = topology.ProcessorCount;
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

                var selectedMask = device.ProcessorMask != 0
                    ? device.ProcessorMask
                    : 1UL << device.Processor;
                var validMask = processorCount == 64 ? ulong.MaxValue : (1UL << processorCount) - 1;
                if (selectedMask == 0 || (selectedMask & ~validMask) != 0)
                    throw new InvalidDataException($"Маска процессоров для {device.Name} выходит за пределы доступной топологии.");
                _ = CreateProcessorMask(Enumerable.Range(0, processorCount)
                    .Where(cpu => (selectedMask & (1UL << cpu)) != 0).ToArray(), processorCount);

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

                RegistryValueKind? maskKind = oldMask is null
                    ? null
                    : existing!.GetValueKind("AssignmentSetOverride");
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
                var selectedMask = device.ProcessorMask != 0 ? device.ProcessorMask : 1UL << device.Processor;
                var mask = BitConverter.GetBytes(selectedMask);
                using var target = Registry.LocalMachine.CreateSubKey(keyPath, writable: true)
                    ?? throw new UnauthorizedAccessException($"Не удалось открыть параметры {device.Name}.");
                target.SetValue("DevicePolicy", SpecifiedProcessorsPolicy, RegistryValueKind.DWord);
                target.SetValue("AssignmentSetOverride", mask, RegistryValueKind.Binary);
            }

            return new(true,
                $"Политики подготовлены для устройств: {devices.Count}. Рекомендации распределены по физическим ядрам; сеть может использовать несколько ядер. Это экспериментальная настройка без гарантии снижения задержек. Перезагрузите Windows, чтобы драйверы перечитали параметры.");
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
            if (key is null) continue;

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

    private sealed record ProcessorCore(int Index, IReadOnlyList<int> LogicalProcessors);
    private sealed record ProcessorTopology(int ProcessorCount, IReadOnlyList<ProcessorCore> Cores);
    private sealed record RegistryBackup(
        string KeyPath, bool HadPolicy, int Policy, string? MaskKind,
        string? MaskBase64, int? MaskDword, long? MaskQword);
}
