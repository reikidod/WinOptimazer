using System.Collections.ObjectModel;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

public sealed class InterruptAffinityCoreOption : ObservableObject
{
    private bool _isSelected;

    public int Index { get; }
    public string Label { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public InterruptAffinityCoreOption(InterruptAffinityCore core, bool isSelected)
    {
        Index = core.Index;
        Label = $"Ядро {core.Index} · CPU {string.Join("/", core.LogicalProcessors)}";
        _isSelected = isSelected;
    }
}

public sealed class InterruptAffinityDeviceItem
{
    private readonly InterruptAffinityDevice _device;

    public string Name => _device.Name;
    public string Category => _device.Category;
    public string Recommendation => _device.Recommendation;
    public string CurrentPolicy => _device.CurrentPolicy;
    public bool CanRestart => InterruptAffinityService.CanRestartDevice(_device);
    public bool RequiresRestartConfirmation => InterruptAffinityService.RequiresRestartConfirmation(_device);
    public string RestartHint => !CanRestart
        ? "Перезапуск заблокирован: это критический контроллер дисков или системный мост."
        : RequiresRestartConfirmation
            ? "Перезапуск может временно отключить USB-устройства, включая мышь и клавиатуру."
            : "Устройство можно перезапустить без полной перезагрузки ПК; связь или изображение могут кратковременно пропасть.";
    public ObservableCollection<InterruptAffinityCoreOption> CoreOptions { get; } = new();

    public InterruptAffinityDeviceItem(InterruptAffinityDevice device)
    {
        _device = device;
        foreach (var core in device.AvailableCores ?? Array.Empty<InterruptAffinityCore>())
        {
            var selected = core.LogicalProcessors.Any(cpu =>
                cpu is >= 0 and < 64 && (device.ProcessorMask & (1UL << cpu)) != 0);
            CoreOptions.Add(new InterruptAffinityCoreOption(core, selected));
        }
    }

    public InterruptAffinityDevice ToDevice()
    {
        var selected = CoreOptions.Where(core => core.IsSelected).Select(core => core.Index).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException($"Для устройства «{Name}» выберите хотя бы одно физическое ядро.");

        var mask = InterruptAffinityService.CreateCoreProcessorMask(
            _device.AvailableCores ?? Array.Empty<InterruptAffinityCore>(), selected, _device.ProcessorCount);
        return _device with { ProcessorMask = mask };
    }

    public InterruptAffinityDevice OriginalDevice => _device;
}

public sealed class InterruptAffinityViewModel : ObservableObject
{
    private readonly InterruptAffinityService _service = new();
    private string _status = "Выполните сканирование, чтобы увидеть совместимые устройства.";
    private bool _isBusy;
    private readonly LatencyTestService _latencyTest = new();
    private LatencyMeasurement? _previousLatencyMeasurement;
    private string _latencyTestStatus = "Тест ещё не запускался.";
    private string _latencyTestResult = "Результаты появятся после первого измерения.";
    private string _latencyTestComparison = "Первый запуск сохранит базовый результат для сравнения.";
    private bool _isLatencyTestRunning;
    public ObservableCollection<InterruptAffinityDeviceItem> Devices { get; } = new();
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public bool IsAdministrator => _service.IsAdministrator;
    public bool HasBackup => _service.HasBackup;
    public System.Windows.Input.ICommand ScanCommand { get; }
    public System.Windows.Input.ICommand ApplyCommand { get; }
    public System.Windows.Input.ICommand RestoreCommand { get; }
    public System.Windows.Input.ICommand RestartDeviceCommand { get; }
    public System.Windows.Input.ICommand RunLatencyTestCommand { get; }
    public System.Windows.Input.ICommand LaunchCoreAffinityCommand { get; }
    public System.Windows.Input.ICommand LaunchInputLatencyCommand { get; }
    public string LatencyTestStatus { get => _latencyTestStatus; private set => SetField(ref _latencyTestStatus, value); }
    public string LatencyTestResult { get => _latencyTestResult; private set => SetField(ref _latencyTestResult, value); }
    public string LatencyTestComparison { get => _latencyTestComparison; private set => SetField(ref _latencyTestComparison, value); }
    public bool IsLatencyTestRunning { get => _isLatencyTestRunning; private set => SetField(ref _isLatencyTestRunning, value); }

    public InterruptAffinityViewModel()
    {
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        ApplyCommand = new RelayCommand(Apply, () => !IsBusy && Devices.Count > 0 && IsAdministrator && !HasBackup);
        RestoreCommand = new RelayCommand(Restore, () => !IsBusy && IsAdministrator && HasBackup);
        RestartDeviceCommand = new AsyncRelayCommand<InterruptAffinityDeviceItem>(
            RestartDeviceAsync, item => !IsBusy && item is { CanRestart: true } && IsAdministrator);
        RunLatencyTestCommand = new AsyncRelayCommand(RunLatencyTestAsync, () => !IsLatencyTestRunning);
        LaunchCoreAffinityCommand = new RelayCommand(() => LaunchDiagnosticTool(DiagnosticToolKind.CoreAffinity));
        LaunchInputLatencyCommand = new RelayCommand(() => LaunchDiagnosticTool(DiagnosticToolKind.InputLatency));
    }

    private void LaunchDiagnosticTool(DiagnosticToolKind kind)
    {
        try
        {
            var window = new DiagnosticConsoleWindow(kind)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            window.Show();
            Status = kind == DiagnosticToolKind.CoreAffinity
                ? "Core Affinity открыт внутри WinOptimizer."
                : "Input Latency открыт внутри WinOptimizer.";
        }
        catch (Exception ex)
        {
            Status = "Не удалось запустить инструмент: " + ex.Message;
            Logger.Error("Не удалось запустить диагностический инструмент " + kind, ex);
        }
    }

    private async Task RunLatencyTestAsync()
    {
        if (IsLatencyTestRunning) return;
        IsLatencyTestRunning = true;
        LatencyTestStatus = "Измеряю задержку таймера Windows…";
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            var result = await _latencyTest.RunAsync(TimeSpan.FromSeconds(5));
            LatencyTestResult = $"Средняя: {result.AverageMicroseconds:0.0} мкс · медиана: {result.MedianMicroseconds:0.0} мкс · P95: {result.P95Microseconds:0.0} мкс · максимум: {result.MaximumMicroseconds:0.0} мкс · выборок: {result.SampleCount:N0}.";
            if (_previousLatencyMeasurement is null)
            {
                LatencyTestComparison = "Это базовый замер. После изменения настроек запустите тест ещё раз.";
            }
            else
            {
                var delta = result.P95Microseconds - _previousLatencyMeasurement.P95Microseconds;
                var direction = Math.Abs(delta) < 100
                    ? "изменение в пределах 0,1 мс — заметного сдвига нет"
                    : delta < 0
                        ? $"P95 ниже на {Math.Abs(delta):0.0} мкс — результат лучше"
                        : $"P95 выше на {delta:0.0} мкс — результат хуже";
                LatencyTestComparison = $"Сравнение с предыдущим запуском: P95 {_previousLatencyMeasurement.P95Microseconds:0.0} → {result.P95Microseconds:0.0} мкс. {direction}.";
            }
            _previousLatencyMeasurement = result;
            LatencyTestStatus = $"Готово · {DateTime.Now:HH:mm:ss} · {(result.UsedHighResolutionTimer ? "высокоточный таймер" : "обычный таймер Windows")}";
        }
        catch (Exception ex)
        {
            LatencyTestStatus = "Тест не выполнен: " + ex.Message;
            Logger.Error("Не удалось выполнить тест аппаратной задержки", ex);
        }
        finally
        {
            IsLatencyTestRunning = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            var found = await Task.Run(_service.Scan);
            Devices.Clear();
            foreach (var device in found) Devices.Add(new InterruptAffinityDeviceItem(device));
            Status = found.Count == 0
                ? "Поддерживаемые устройства не обнаружены или доступно менее двух логических процессоров."
                : $"Найдено устройств: {found.Count}. Выберите физические ядра вручную или оставьте рекомендации.";
        }
        catch (Exception ex) { Status = "Ошибка сканирования: " + ex.Message; }
        finally { IsBusy = false; RefreshCommands(); }
    }

    private void Apply()
    {
        try
        {
            var result = _service.Apply(Devices.Select(item => item.ToDevice()).ToArray());
            Status = result.Message;
        }
        catch (Exception ex)
        {
            Status = "Не удалось подготовить выбранные ядра: " + ex.Message;
        }
        OnPropertyChanged(nameof(HasBackup));
        RefreshCommands();
    }

    private async Task RestartDeviceAsync(InterruptAffinityDeviceItem? item)
    {
        if (item is null) return;
        if (item.RequiresRestartConfirmation)
        {
            var answer = System.Windows.MessageBox.Show(
                $"Перезапустить «{item.Name}»?\n\nUSB-контроллер может на несколько секунд отключить мышь, клавиатуру, гарнитуру и другие USB-устройства. Не выполняйте это во время передачи файлов или обновления прошивки.",
                "Подтверждение перезапуска устройства",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes)
                return;
        }

        IsBusy = true;
        RefreshCommands();
        try
        {
            var result = await Task.Run(() => _service.RestartDevice(item.OriginalDevice));
            Status = result.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    private void Restore()
    {
        var result = _service.Restore();
        Status = result.Message;
        OnPropertyChanged(nameof(HasBackup));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        OnPropertyChanged(nameof(HasBackup));
    }
}
