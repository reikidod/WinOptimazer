using System.Collections.ObjectModel;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;

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
    public string RestartHint => CanRestart
        ? "Можно перезапустить без полной перезагрузки ПК; изображение или сеть могут кратковременно пропасть."
        : "Перезапуск заблокирован для безопасности: устройство может отключить ввод, накопители или системную шину.";
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
    public ObservableCollection<InterruptAffinityDeviceItem> Devices { get; } = new();
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public bool IsAdministrator => _service.IsAdministrator;
    public bool HasBackup => _service.HasBackup;
    public System.Windows.Input.ICommand ScanCommand { get; }
    public System.Windows.Input.ICommand ApplyCommand { get; }
    public System.Windows.Input.ICommand RestoreCommand { get; }
    public System.Windows.Input.ICommand RestartDeviceCommand { get; }

    public InterruptAffinityViewModel()
    {
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        ApplyCommand = new RelayCommand(Apply, () => !IsBusy && Devices.Count > 0 && IsAdministrator && !HasBackup);
        RestoreCommand = new RelayCommand(Restore, () => !IsBusy && IsAdministrator && HasBackup);
        RestartDeviceCommand = new AsyncRelayCommand<InterruptAffinityDeviceItem>(
            RestartDeviceAsync, item => !IsBusy && item is { CanRestart: true } && IsAdministrator);
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
