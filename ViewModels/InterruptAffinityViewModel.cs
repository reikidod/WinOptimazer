using System.Collections.ObjectModel;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;

namespace WinOptimizer.ViewModels;

public sealed class InterruptAffinityViewModel : ObservableObject
{
    private readonly InterruptAffinityService _service = new();
    private string _status = "Выполните сканирование, чтобы увидеть совместимые устройства.";
    private bool _isBusy;
    public ObservableCollection<InterruptAffinityDevice> Devices { get; } = new();
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public bool IsAdministrator => _service.IsAdministrator;
    public bool HasBackup => _service.HasBackup;
    public System.Windows.Input.ICommand ScanCommand { get; }
    public System.Windows.Input.ICommand ApplyCommand { get; }
    public System.Windows.Input.ICommand RestoreCommand { get; }

    public InterruptAffinityViewModel()
    {
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        ApplyCommand = new RelayCommand(Apply, () => !IsBusy && Devices.Count > 0 && IsAdministrator && !HasBackup);
        RestoreCommand = new RelayCommand(Restore, () => !IsBusy && IsAdministrator && HasBackup);
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            var found = await Task.Run(_service.Scan);
            Devices.Clear();
            foreach (var device in found) Devices.Add(device);
            Status = found.Count == 0
                ? "Поддерживаемые устройства не обнаружены или доступно менее двух логических процессоров."
                : $"Найдено устройств: {found.Count}. Подбор предварительный; проверьте рекомендации перед применением.";
        }
        catch (Exception ex) { Status = "Ошибка сканирования: " + ex.Message; }
        finally { IsBusy = false; RefreshCommands(); }
    }

    private void Apply()
    {
        var result = _service.Apply(Devices.ToArray());
        Status = result.Message;
        OnPropertyChanged(nameof(HasBackup));
        RefreshCommands();
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
        (ScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasBackup));
    }
}
