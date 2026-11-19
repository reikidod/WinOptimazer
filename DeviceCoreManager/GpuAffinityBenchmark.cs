using System.Diagnostics;
using System.Globalization;
using System.Text;
using WinOptimizer.Services;

namespace WinOptimizer.DeviceCoreManager;

internal sealed record GpuCoreBenchmarkResult(int PhysicalCore, GpuBenchmarkMetrics Metrics);

internal static class GpuAffinityBenchmark
{
    private const int BenchmarkOffsetMs = 5000;
    private const int DeviceRestartWaitSeconds = 8;
    private const double MinimumImprovementPercent = 2.0;
    private const double MaximumFinalRegressionPercent = 3.0;

    public static int RunInteractive(InterruptAffinityService service)
    {
        Console.WriteLine();
        Console.WriteLine("GPU CORE AFFINITY BENCHMARK — liblava");
        Console.WriteLine("Сравнение исходной конфигурации и каждого физического ядра CPU.");
        Console.WriteLine("Перед каждым вариантом будет перезапущен только графический PnP-адаптер.");
        Console.WriteLine("Экран может погаснуть на несколько секунд; GPU-приложения могут быть прерваны.");
        Console.WriteLine();

        if (!service.IsAdministrator)
        {
            Console.WriteLine("Запустите утилиту от имени администратора.");
            return 5;
        }
        if (service.HasBackup)
        {
            Console.WriteLine("Найдена резервная копия предыдущей настройки. Сначала выполните --restore.");
            return 6;
        }

        var devices = service.Scan()
            .Where(device => device.Category == "Графика" &&
                             device.DeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (devices.Length == 0)
        {
            Console.WriteLine("Совместимый PCI графический адаптер не найден.");
            return 2;
        }

        Console.WriteLine("Графические адаптеры Windows:");
        for (var index = 0; index < devices.Length; index++)
            Console.WriteLine($"  {index}. {devices[index].Name} [{devices[index].DeviceId}]");
        var adapterIndex = ReadInt("Номер адаптера Windows для настройки [0]: ", 0, 0, devices.Length - 1);
        var selectedDevice = devices[adapterIndex];
        Console.WriteLine($"Выбран адаптер: {selectedDevice.Name}");
        Console.WriteLine();
        Console.Write("Путь к lava-triangle.exe (liblava): ");
        var lavaPath = NormalizePath(Console.ReadLine());
        if (lavaPath is null || !File.Exists(lavaPath))
        {
            Console.WriteLine("Файл lava-triangle.exe не найден.");
            return 2;
        }

        var physicalDevice = ReadInt("Индекс Vulkan GPU в liblava [0]: ", 0, 0, 15);
        var trials = ReadInt("Количество проходов на ядро [3]: ", 3, 1, 5);
        var duration = ReadInt("Длительность одного прохода в секундах [30]: ", 30, 15, 120);
        var selectedDevices = new[] { selectedDevice };
        var cores = selectedDevice.AvailableCores ?? Array.Empty<InterruptAffinityCore>();
        var estimatedMinutes = ((duration + 13) * (1 + cores.Count * trials)) / 60.0;
        Console.WriteLine();
        Console.WriteLine($"Физических ядер: {cores.Count}; выбран один GPU-адаптер.");
        Console.WriteLine($"Проходов на ядро: {trials}; длительность: {duration} сек.");
        Console.WriteLine($"Оценка времени: около {estimatedMinutes:F0} мин. плюс перезапуски драйвера.");
        Console.WriteLine("Закройте игры, запись экрана и другие GPU-нагрузки. Сохраните документы.");
        Console.WriteLine();
        Console.Write("Для запуска и перезапуска GPU введите BENCHMARK: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "BENCHMARK", StringComparison.Ordinal))
        {
            Console.WriteLine("Тест отменён. Настройки не изменены.");
            return 0;
        }

        return Run(service, selectedDevices, lavaPath, physicalDevice, trials, duration);
    }


    private static int Run(InterruptAffinityService service, IReadOnlyList<InterruptAffinityDevice> devices,
        string lavaPath, int physicalDevice, int trials, int durationSeconds)
    {
        var outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinOptimizer", "GpuAffinityBenchmarks", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outputDirectory);
        var results = new List<GpuCoreBenchmarkResult>();
        var backupActive = false;

        try
        {
            Console.WriteLine();
            Console.WriteLine("ЭТАП 1/3 — исходный замер текущей конфигурации");
            var baseline = Aggregate(RunTrials(lavaPath, outputDirectory, "baseline", physicalDevice, trials, durationSeconds));

            var cores = devices[0].AvailableCores ?? Array.Empty<InterruptAffinityCore>();
            for (var index = 0; index < cores.Count; index++)
            {
                var core = cores[index];
                var mask = core.LogicalProcessors.Aggregate(0UL, (current, cpu) => current | (1UL << cpu));
                if (mask == 0) throw new InvalidDataException($"Для физического ядра {core.Index} получена пустая маска.");

                Console.WriteLine();
                Console.WriteLine($"ЭТАП 2/3 — физическое ядро {core.Index} ({index + 1}/{cores.Count})");
                var configuration = devices.Select(device => device with
                {
                    Processor = core.LogicalProcessors[0],
                    ProcessorMask = mask
                }).ToArray();
                var apply = service.Apply(configuration);
                if (!apply.Success) throw new InvalidOperationException("Не удалось применить тестовую affinity: " + apply.Message);
                backupActive = true;

                RestartAdapters(service, devices);
                var measurements = RunTrials(lavaPath, outputDirectory, $"core-{core.Index}", physicalDevice, trials, durationSeconds);
                results.Add(new GpuCoreBenchmarkResult(core.Index, Aggregate(measurements)));

                var restore = service.Restore();
                if (!restore.Success) throw new InvalidOperationException("Не удалось восстановить исходную affinity: " + restore.Message);
                backupActive = false;
                RestartAdapters(service, devices);
            }

            var best = results.OrderByDescending(item => item.Metrics.OnePercentLowFps).First();
            var improvement = PercentChange(baseline.OnePercentLowFps, best.Metrics.OnePercentLowFps);
            Console.WriteLine();
            Console.WriteLine("СВОДКА ПО ЯДРАМ");
            PrintResult("Исходная конфигурация", baseline);
            foreach (var result in results.OrderByDescending(item => item.Metrics.OnePercentLowFps))
                PrintResult($"Физическое ядро {result.PhysicalCore}", result.Metrics);
            Console.WriteLine($"Лучшее ядро: {best.PhysicalCore}; изменение 1% Low: {improvement:+0.00;-0.00;0.00}%");

            if (improvement < MinimumImprovementPercent)
            {
                Console.WriteLine($"Улучшение меньше порога {MinimumImprovementPercent:F1}%. Привязка не будет оставлена.");
                WriteSummary(outputDirectory, baseline, results, null, "No stable improvement; original settings restored.");
                Console.WriteLine($"Отчёт: {outputDirectory}");
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine("ЭТАП 3/3 — применение лучшего ядра и контрольный замер");
            var selectedCore = cores.First(item => item.Index == best.PhysicalCore);
            var selectedMask = selectedCore.LogicalProcessors.Aggregate(0UL, (current, cpu) => current | (1UL << cpu));
            var finalConfiguration = devices.Select(device => device with
            {
                Processor = selectedCore.LogicalProcessors[0],
                ProcessorMask = selectedMask
            }).ToArray();
            var finalApply = service.Apply(finalConfiguration);
            if (!finalApply.Success) throw new InvalidOperationException("Не удалось применить итоговую affinity: " + finalApply.Message);
            backupActive = true;
            RestartAdapters(service, devices);

            var finalMetrics = Aggregate(RunTrials(lavaPath, outputDirectory, "final-selected-core", physicalDevice, trials, durationSeconds));
            PrintResult("Контрольный замер после применения", finalMetrics);
            var finalImprovement = PercentChange(baseline.OnePercentLowFps, finalMetrics.OnePercentLowFps);
            if (finalImprovement < -MaximumFinalRegressionPercent)
            {
                Console.WriteLine("Контрольный замер показал ухудшение. Выполняется автоматический откат.");
                var restore = service.Restore();
                if (!restore.Success) throw new InvalidOperationException("Автоматический откат не завершился: " + restore.Message);
                backupActive = false;
                RestartAdapters(service, devices);
                WriteSummary(outputDirectory, baseline, results, finalMetrics, "Final verification regressed; original settings restored.");
                return 1;
            }

            WriteSummary(outputDirectory, baseline, results, finalMetrics,
                $"Applied physical core {best.PhysicalCore}; backup retained for manual restore.");
            Console.WriteLine();
            Console.WriteLine($"Итоговая привязка оставлена на физическом ядре {best.PhysicalCore}.");
            Console.WriteLine("Исходная конфигурация сохранена. Для отката выполните --restore от имени администратора.");
            Console.WriteLine($"Отчёт: {outputDirectory}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Тест остановлен: " + ex.Message);
            if (backupActive || service.HasBackup)
            {
                var restore = service.Restore();
                Console.Error.WriteLine("Откат: " + restore.Message);
                if (restore.Success)
                {
                    try { RestartAdapters(service, devices); }
                    catch (Exception restartError) { Console.Error.WriteLine("Не удалось перезапустить GPU после отката: " + restartError.Message); }
                }
                else Console.Error.WriteLine("Резервная копия сохранена. Повторите --restore от имени администратора.");
            }
            return 1;
        }
    }

    private static List<GpuBenchmarkMetrics> RunTrials(string lavaPath, string outputDirectory,
        string label, int physicalDevice, int trials, int durationSeconds)
    {
        var metrics = new List<GpuBenchmarkMetrics>();
        for (var trial = 1; trial <= trials; trial++)
        {
            var jsonPath = Path.Combine(outputDirectory, $"{label}-trial-{trial}.json");
            Console.WriteLine($"  Проход {trial}/{trials}: прогрев {BenchmarkOffsetMs / 1000} сек., захват {durationSeconds} сек.");
            RunSingleTrial(lavaPath, jsonPath, physicalDevice, durationSeconds);
            var result = LiblavaBenchmarkParser.Parse(File.ReadAllText(jsonPath));
            metrics.Add(result);
            PrintResult($"  Проход {trial}", result);
        }
        return metrics;
    }

    private static void RunSingleTrial(string lavaPath, string jsonPath, int physicalDevice, int durationSeconds)
    {
        if (File.Exists(jsonPath)) File.Delete(jsonPath);
        using var benchmark = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = lavaPath,
                WorkingDirectory = Path.GetDirectoryName(lavaPath)!,
                UseShellExecute = false,
                CreateNoWindow = false
            }
        };
        foreach (var argument in new[]
        {
            "--benchmark",
            $"--physical_device={physicalDevice}",
            $"--benchmark_time={durationSeconds * 1000}",
            $"--benchmark_offset={BenchmarkOffsetMs}",
            "--benchmark_exit=1",
            $"--benchmark_file={Path.GetFileName(jsonPath)}",
            $"--benchmark_path={Path.GetDirectoryName(jsonPath)}"
        })
            benchmark.StartInfo.ArgumentList.Add(argument);

        if (!benchmark.Start()) throw new InvalidOperationException("Не удалось запустить lava-triangle.exe.");
        if (!benchmark.WaitForExit((durationSeconds + BenchmarkOffsetMs / 1000 + 45) * 1000))
        {
            benchmark.Kill(entireProcessTree: true);
            throw new TimeoutException("liblava не завершил benchmark в отведённое время.");
        }
        if (benchmark.ExitCode != 0)
            throw new InvalidOperationException($"liblava завершился с кодом {benchmark.ExitCode}.");
        if (!File.Exists(jsonPath) || new FileInfo(jsonPath).Length == 0)
            throw new InvalidDataException("liblava не создал JSON-файл результатов.");
    }

    private static void RestartAdapters(InterruptAffinityService service, IReadOnlyList<InterruptAffinityDevice> devices)
    {
        foreach (var device in devices)
        {
            Console.WriteLine($"  Перезапуск графического адаптера: {device.Name}");
            var result = service.RestartDevice(device);
            if (!result.Success) throw new InvalidOperationException(result.Message);
        }
        Task.Delay(TimeSpan.FromSeconds(DeviceRestartWaitSeconds)).GetAwaiter().GetResult();
    }

    private static GpuBenchmarkMetrics Aggregate(IReadOnlyList<GpuBenchmarkMetrics> values)
    {
        if (values.Count == 0) throw new InvalidDataException("Нет результатов измерений.");
        return new GpuBenchmarkMetrics(values.Average(item => item.AverageFps),
            values.Average(item => item.OnePercentLowFps), values.Average(item => item.P99FrameTimeMs),
            values.Sum(item => item.FrameCount));
    }

    private static double PercentChange(double baseline, double current) =>
        baseline <= 0 ? 0 : (current - baseline) / baseline * 100.0;

    private static void PrintResult(string label, GpuBenchmarkMetrics metrics) =>
        Console.WriteLine($"{label}: Avg {metrics.AverageFps:F1} FPS | 1% Low {metrics.OnePercentLowFps:F1} FPS | P99 {metrics.P99FrameTimeMs:F2} ms | кадров {metrics.FrameCount}");

    private static void WriteSummary(string directory, GpuBenchmarkMetrics baseline,
        IReadOnlyList<GpuCoreBenchmarkResult> results, GpuBenchmarkMetrics? final, string status)
    {
        var lines = new List<string>
        {
            "WinOptimizer GPU Core Affinity Benchmark",
            $"Created: {DateTimeOffset.Now:O}",
            $"Status: {status}",
            $"Baseline Avg FPS: {baseline.AverageFps:F3}",
            $"Baseline 1% Low FPS: {baseline.OnePercentLowFps:F3}",
            $"Baseline P99 ms: {baseline.P99FrameTimeMs:F3}",
            "",
            "Core,AverageFps,OnePercentLowFps,P99FrameTimeMs,FrameCount"
        };
        lines.AddRange(results.Select(item =>
            $"{item.PhysicalCore},{item.Metrics.AverageFps:F3},{item.Metrics.OnePercentLowFps:F3},{item.Metrics.P99FrameTimeMs:F3},{item.Metrics.FrameCount}"));
        if (final is not null)
        {
            lines.Add("");
            lines.Add($"Final Avg FPS: {final.AverageFps:F3}");
            lines.Add($"Final 1% Low FPS: {final.OnePercentLowFps:F3}");
            lines.Add($"Final P99 ms: {final.P99FrameTimeMs:F3}");
        }
        File.WriteAllLines(Path.Combine(directory, "summary.txt"), lines, Encoding.UTF8);
    }

    private static string? NormalizePath(string? value)
    {
        var path = value?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    private static int ReadInt(string prompt, int defaultValue, int min, int max)
    {
        Console.Write(prompt);
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) return defaultValue;
        return int.TryParse(input, out var value) && value >= min && value <= max
            ? value
            : ReadInt($"Введите число от {min} до {max} [{defaultValue}]: ", defaultValue, min, max);
    }
}
