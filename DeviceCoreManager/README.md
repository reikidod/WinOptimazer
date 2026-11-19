# WinOptimizer Device Core Manager

Отдельная консольная утилита Windows для просмотра и настройки **interrupt affinity** — распределения аппаратных прерываний устройств по физическим ядрам CPU.

## Сборка

Из корня репозитория:

```powershell
dotnet restore DeviceCoreManager/WinOptimizer.DeviceCoreManager.csproj
dotnet build DeviceCoreManager/WinOptimizer.DeviceCoreManager.csproj -c Release
dotnet publish DeviceCoreManager/WinOptimizer.DeviceCoreManager.csproj -c Release --self-contained false -o artifacts/DeviceCoreManager
```

Исполняемый файл после сборки:

`DeviceCoreManager/bin/Release/net8.0-windows/win-x64/WinOptimizer.DeviceCoreManager.exe`

Готовый ZIP публикуется как артефакт workflow **Device Core Manager CI** в GitHub Actions. Сборка framework-dependent: для запуска требуется .NET 8 Runtime x64.

## Использование

- Без аргументов — интерактивное меню.
- `--scan` — только обнаружение устройств и показ рекомендаций, без изменений.
- `--configure` — интерактивный выбор физических ядер для каждого устройства.
- `--restore` — возврат сохранённых исходных параметров.
- `--help` — справка.

Для применения и восстановления параметров требуется запуск терминала от имени администратора. Перед изменением программа сохраняет резервную копию в общем каталоге WinOptimizer в профиле пользователя. Если проверка записанных значений не проходит, утилита автоматически пытается выполнить откат.

## Архитектура и ограничения

- Утилита собирается отдельно от WPF-приложения и не запускает его интерфейс.
- Для исключения расхождения логики она компилирует тот же исходный файл `Services/InterruptAffinityService.cs`, который использует WinOptimizer.
- Текущий механизм поддерживает одну активную группу процессоров и до 64 логических процессоров в ней. При другой топологии сервис откажется от изменения настроек.
- Утилита настраивает политику аппаратных прерываний устройств, а не affinity обычных процессов.
- Настройки обычно перечитываются драйверами после перезагрузки Windows. Утилита не перезапускает устройства автоматически.
- Рекомендации не гарантируют повышение FPS или снижение DPC/ISR latency. Результат следует измерять отдельно.
- Накопители и системные контроллеры не перезапускаются этой утилитой.


## GPU Core Affinity Benchmark (liblava)

The interactive menu contains **4. GPU benchmark**. It compares the current configuration with one physical CPU core at a time using liblava's built-in Vulkan benchmark mode. The workload writes frame durations to JSON with `--benchmark`, `--benchmark_time`, `--benchmark_offset`, `--benchmark_file`, and `--benchmark_exit`.

Required external tool (not bundled):
- `lava-triangle.exe` built from liblava with benchmark support. Choose the Windows display adapter, enter the matching Vulkan physical-device index (default 0), and provide the full path to `lava-triangle.exe`.

The benchmark runs a baseline and then each physical core for 1–5 trials (30 seconds recommended). Before each candidate and after restoring the original settings, it restarts only the selected PCI graphics adapter through Windows PnPUtil. The display may go black briefly. It does not reboot Windows.

Metrics: average FPS, 1% low FPS (mean of the slowest 1% frame times) and P99 frame time. A candidate is not kept unless its 1% low improves by at least 2% over baseline. After applying the best candidate, a control run is performed; if it regresses more than 3%, the original settings are restored automatically.

If the final candidate is retained, the original registry configuration remains backed up. Run `WinOptimizer.DeviceCoreManager.exe --restore` as administrator to restore it. If the program is interrupted during a candidate run, run `--restore` before another benchmark.

Reports and raw JSON captures are saved under:
`%LOCALAPPDATA%\WinOptimizer\GpuAffinityBenchmarks`

The workflow is inspired by the public AutoGpuAffinity approach:
https://github.com/VladosVladi/AutoGpuAffinity
https://github.com/liblava/liblava

**Safety:** restarting a display adapter can interrupt graphics sessions or cause the driver to recover slowly. Save work and close GPU workloads before testing. Results do not guarantee improved gaming performance.
