using System.Globalization;
using Microsoft.Win32;
using WinOptimizer.Services;

namespace WinOptimizer.DeviceCoreManager;

internal static class Program
{
    private const string AffinitySuffix = @"Device Parameters\Interrupt Management\Affinity Policy";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Device Core Manager поддерживает только Windows 10/11.");
            return 2;
        }

        var service = new InterruptAffinityService();
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();

        try
        {
            return command switch
            {
                "--help" or "-h" or "/?" => ShowHelp(),
                "--scan" => Scan(service),
                "--configure" => Configure(service),
                "--gpu-benchmark" => GpuAffinityBenchmark.RunInteractive(service),
                "--restore" => Restore(service),
                null => Interactive(service),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Критическая ошибка: {ex.Message}");
            return 1;
        }
    }

    private static int Interactive(InterruptAffinityService service)
    {
        while (true)
        {
            Console.Clear();
            Header();
            Console.WriteLine($"Права администратора: {(service.IsAdministrator ? "да" : "нет")}");
            Console.WriteLine($"Резервная копия: {(service.HasBackup ? "есть" : "нет")}");
            Console.WriteLine();
            Console.WriteLine("  1. Сканировать устройства и показать рекомендации");
            Console.WriteLine("  2. Настроить привязку ядер к устройствам");
            Console.WriteLine("  3. Восстановить исходные настройки");
            Console.WriteLine("  4. GPU benchmark: подбор ядра через liblava");
            Console.WriteLine("  0. Выход");
            Console.Write("\nВыберите действие: ");

            switch (Console.ReadLine()?.Trim())
            {
                case "1":
                    Scan(service);
                    Pause();
                    break;
                case "2":
                    Configure(service);
                    Pause();
                    break;
                case "3":
                    Restore(service);
                    Pause();
                    break;
                case "4":
                    GpuAffinityBenchmark.RunInteractive(service);
                    Pause();
                    break;
                case "0":
                    return 0;
                default:
                    Console.WriteLine("Неизвестный пункт меню.");
                    Pause();
                    break;
            }
        }
    }

    private static int Scan(InterruptAffinityService service)
    {
        Header();
        Console.WriteLine("СКАНИРОВАНИЕ УСТРОЙСТВ");
        var devices = service.Scan();
        if (devices.Count == 0)
        {
            Console.WriteLine("Совместимые устройства не найдены.");
            return 0;
        }

        Console.WriteLine($"Найдено устройств: {devices.Count}");
        Console.WriteLine($"Логических процессоров: {devices[0].ProcessorCount}");
        Console.WriteLine();
        PrintDevices(devices);
        return 0;
    }

    private static int Configure(InterruptAffinityService service)
    {
        Header();
        Console.WriteLine("НАСТРОЙКА ПРИВЯЗКИ ПРЕРЫВАНИЙ");
        if (!service.IsAdministrator)
        {
            Console.WriteLine("Для изменения параметров запустите терминал от имени администратора.");
            return 5;
        }

        if (service.HasBackup)
        {
            Console.WriteLine("Уже существует резервная копия предыдущей конфигурации.");
            Console.WriteLine("Сначала выполните откат командой --restore, затем повторите настройку.");
            return 6;
        }

        var devices = service.Scan();
        if (devices.Count == 0)
        {
            Console.WriteLine("Совместимые устройства не найдены.");
            return 0;
        }

        PrintDevices(devices);
        Console.WriteLine();
        Console.WriteLine("Для каждого устройства укажите физические ядра через запятую.");
        Console.WriteLine("Enter — рекомендуемые ядра; '-' — пропустить устройство.");
        Console.WriteLine();

        var selectedDevices = new List<InterruptAffinityDevice>();
        foreach (var device in devices)
        {
            var recommended = GetRecommendedCoreIndexes(device);
            Console.Write($"{device.Name} [{device.Category}] (рекомендация: {FormatCores(recommended)}): ");
            var input = Console.ReadLine()?.Trim();

            if (input == "-")
                continue;

            int[] selectedCores;
            if (string.IsNullOrWhiteSpace(input))
            {
                selectedCores = recommended;
            }
            else if (!TryParseCoreIndexes(input, device, out selectedCores, out var error))
            {
                Console.WriteLine($"  Пропуск устройства: {error}");
                continue;
            }

            if (!TryCreateMask(device, selectedCores, out var mask, out var maskError))
            {
                Console.WriteLine($"  Пропуск устройства: {maskError}");
                continue;
            }

            selectedDevices.Add(device with
            {
                Processor = FirstLogicalProcessor(device, selectedCores),
                ProcessorMask = mask
            });
            Console.WriteLine($"  Выбрано: {FormatCores(selectedCores)}");
        }

        if (selectedDevices.Count == 0)
        {
            Console.WriteLine("Ни одно устройство не выбрано. Изменений нет.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Будут изменены параметры политики прерываний в HKLM.");
        Console.WriteLine("Исходные значения сохранятся в резервную копию.");
        Console.WriteLine("Настройки драйверов обычно вступают в силу после перезагрузки Windows.");
        Console.WriteLine("Утилита не перезапускает устройства и не обещает прирост FPS или снижение задержки.");
        Console.WriteLine();
        Console.Write($"Для применения введите APPLY ({selectedDevices.Count} устройств): ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "APPLY", StringComparison.Ordinal))
        {
            Console.WriteLine("Отменено. Системные настройки не изменены.");
            return 0;
        }

        var result = service.Apply(selectedDevices);
        Console.WriteLine(result.Message);
        if (!result.Success)
            return 1;

        bool verified;
        string verificationError;
        try
        {
            verified = VerifyApplied(selectedDevices, out verificationError);
        }
        catch (Exception ex)
        {
            verified = false;
            verificationError = ex.Message;
        }

        if (verified)
        {
            Console.WriteLine("Проверка реестра: успешно. DevicePolicy и маски процессоров совпадают с выбранными значениями.");
            Console.WriteLine("Резервная копия сохранена. Для возврата исходных настроек используйте пункт 3 или --restore.");
            return 0;
        }

        Console.Error.WriteLine("Проверка после записи не пройдена: " + verificationError);
        var rollback = service.Restore();
        Console.Error.WriteLine(rollback.Message);
        return rollback.Success ? 1 : 7;
    }

    private static int Restore(InterruptAffinityService service)
    {
        Header();
        if (!service.IsAdministrator)
        {
            Console.WriteLine("Для восстановления исходных параметров запустите терминал от имени администратора.");
            return 5;
        }

        var result = service.Restore();
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }

    private static bool VerifyApplied(
        IReadOnlyList<InterruptAffinityDevice> devices,
        out string error)
    {
        foreach (var device in devices)
        {
            var path = @"SYSTEM\CurrentControlSet\Enum\" + device.DeviceId + "\\" + AffinitySuffix;
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null)
            {
                error = $"Не удалось повторно открыть параметры «{device.Name}».";
                return false;
            }

            if (key.GetValue("DevicePolicy") is not int policy || policy != 4)
            {
                error = $"Для «{device.Name}» DevicePolicy не равен 4.";
                return false;
            }

            if (key.GetValue("AssignmentSetOverride") is not byte[] actual ||
                !actual.SequenceEqual(BitConverter.GetBytes(device.ProcessorMask)))
            {
                error = $"Маска AssignmentSetOverride для «{device.Name}» не совпадает с выбранной.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static int[] GetRecommendedCoreIndexes(InterruptAffinityDevice device)
    {
        var cores = device.AvailableCores ?? Array.Empty<InterruptAffinityCore>();
        return cores
            .Where(core => core.LogicalProcessors.Any(cpu =>
                cpu is >= 0 and < 64 && (device.ProcessorMask & (1UL << cpu)) != 0))
            .Select(core => core.Index)
            .ToArray();
    }

    private static bool TryParseCoreIndexes(
        string input,
        InterruptAffinityDevice device,
        out int[] indexes,
        out string error)
    {
        indexes = Array.Empty<int>();
        error = string.Empty;
        var available = (device.AvailableCores ?? Array.Empty<InterruptAffinityCore>())
            .Select(core => core.Index)
            .ToHashSet();

        var tokens = input.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Any(string.IsNullOrWhiteSpace))
        {
            error = "Разделяйте номера ядер запятыми; пустые элементы недопустимы.";
            return false;
        }

        var parsed = new HashSet<int>();
        foreach (var token in tokens)
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                !available.Contains(index))
            {
                error = $"«{token}» не является доступным номером физического ядра.";
                return false;
            }

            parsed.Add(index);
        }

        if (parsed.Count == 0)
        {
            error = "Выберите хотя бы одно физическое ядро.";
            return false;
        }

        indexes = parsed.Order().ToArray();
        return true;
    }

    private static bool TryCreateMask(
        InterruptAffinityDevice device,
        IReadOnlyCollection<int> selectedCoreIndexes,
        out ulong mask,
        out string error)
    {
        mask = 0;
        error = string.Empty;
        var cores = device.AvailableCores ?? Array.Empty<InterruptAffinityCore>();
        foreach (var index in selectedCoreIndexes)
        {
            var core = cores.FirstOrDefault(item => item.Index == index);
            if (core is null)
            {
                error = $"Физическое ядро {index} не найдено.";
                return false;
            }

            foreach (var cpu in core.LogicalProcessors)
            {
                if (cpu < 0 || cpu >= device.ProcessorCount || cpu >= 64)
                {
                    error = $"Логический процессор {cpu} находится вне поддерживаемой группы.";
                    return false;
                }

                mask |= 1UL << cpu;
            }
        }

        if (mask == 0)
        {
            error = "Получена пустая маска процессоров.";
            return false;
        }

        return true;
    }

    private static int FirstLogicalProcessor(
        InterruptAffinityDevice device,
        IReadOnlyCollection<int> selectedCoreIndexes) =>
        (device.AvailableCores ?? Array.Empty<InterruptAffinityCore>())
            .Where(core => selectedCoreIndexes.Contains(core.Index))
            .SelectMany(core => core.LogicalProcessors)
            .DefaultIfEmpty(0)
            .Min();

    private static string FormatCores(IEnumerable<int> indexes) =>
        string.Join(", ", indexes.Select(index => $"ядро {index}"));

    private static void PrintDevices(IReadOnlyList<InterruptAffinityDevice> devices)
    {
        foreach (var device in devices)
        {
            Console.WriteLine($"[{device.Category}] {device.Name}");
            Console.WriteLine($"  ID: {device.DeviceId}");
            Console.WriteLine($"  Политика: {device.CurrentPolicy}");
            Console.WriteLine($"  Рекомендация: {device.Recommendation}");
            Console.WriteLine($"  Физические ядра: {string.Join("; ", (device.AvailableCores ?? Array.Empty<InterruptAffinityCore>()).Select(core => $"{core.Index} (CPU {string.Join("/", core.LogicalProcessors)})"))}");
        }
    }

    private static void Header()
    {
        Console.WriteLine("============================================================");
        Console.WriteLine(" WinOptimizer Device Core Manager");
        Console.WriteLine(" Управление CPU affinity для аппаратных прерываний устройств");
        Console.WriteLine("============================================================");
        Console.WriteLine();
    }

    private static int ShowHelp()
    {
        Header();
        Console.WriteLine("Использование:");
        Console.WriteLine("  WinOptimizer.DeviceCoreManager.exe           Интерактивное меню");
        Console.WriteLine("  WinOptimizer.DeviceCoreManager.exe --scan    Только чтение и рекомендации");
        Console.WriteLine("  WinOptimizer.DeviceCoreManager.exe --configure Интерактивная настройка");
        Console.WriteLine("  WinOptimizer.DeviceCoreManager.exe --gpu-benchmark Тест GPU на liblava");
        Console.WriteLine("  WinOptimizer.DeviceCoreManager.exe --restore Восстановление резервной копии");
        Console.WriteLine("  WinOptimizer.DeviceCoreManager.exe --help    Справка");
        Console.WriteLine();
        Console.WriteLine("Изменение параметров требует запуска от имени администратора.");
        return 0;
    }

    private static int UnknownCommand(string? command)
    {
        Console.Error.WriteLine($"Неизвестная команда: {command}");
        ShowHelp();
        return 2;
    }

    private static void Pause()
    {
        Console.WriteLine();
        Console.Write("Нажмите Enter для продолжения...");
        Console.ReadLine();
    }
}
