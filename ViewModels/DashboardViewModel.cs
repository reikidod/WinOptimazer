using System.Collections.ObjectModel;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;

namespace WinOptimizer.ViewModels;

/// <summary>Главная страница: снимок системы, рекомендации, последние операции, быстрые действия.</summary>
public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly ISystemMonitorService _monitor;
    private readonly IOperationHistoryService _history;
    private readonly IOptimizationProfileService _profiles;
    private readonly Action<AppPage> _navigate;
    private bool _active;
    private string _cpuText = "—";
    private string _ramText = "—";
    private string _cpuTempText = "—";
    private string _gpuTempText = "—";
    private string _statusSummary = "Загрузка…";

    public DashboardViewModel(
        ISystemMonitorService monitor,
        IOperationHistoryService history,
        IOptimizationProfileService profiles,
        Action<AppPage> navigate)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));

        OpenTweaksCommand = new RelayCommand(() => _navigate(AppPage.Tweaks));
        OpenMaintenanceCommand = new RelayCommand(() => _navigate(AppPage.Maintenance));
        OpenGamesCommand = new RelayCommand(() => _navigate(AppPage.Games));
        OpenSystemCommand = new RelayCommand(() => _navigate(AppPage.System));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);

        Profiles = new ObservableCollection<OptimizationProfile>(_profiles.BuiltIn);
        RefreshHistory();
    }

    public ObservableCollection<OptimizationProfile> Profiles { get; }

    public ObservableCollection<OperationRecord> RecentOperations { get; } = new();

    public string CpuText { get => _cpuText; private set => SetField(ref _cpuText, value); }
    public string RamText { get => _ramText; private set => SetField(ref _ramText, value); }
    public string CpuTempText { get => _cpuTempText; private set => SetField(ref _cpuTempText, value); }
    public string GpuTempText { get => _gpuTempText; private set => SetField(ref _gpuTempText, value); }
    public string StatusSummary { get => _statusSummary; private set => SetField(ref _statusSummary, value); }

    public ICommand OpenTweaksCommand { get; }
    public ICommand OpenMaintenanceCommand { get; }
    public ICommand OpenGamesCommand { get; }
    public ICommand OpenSystemCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <summary>Включает опрос только пока открыта главная.</summary>
    public void OnActivated()
    {
        if (_active) return;
        _active = true;
        _monitor.Updated += OnSnapshot;
        _monitor.Start(TimeSpan.FromSeconds(3));
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
        if (!_active) return;
        _active = false;
        _monitor.Updated -= OnSnapshot;
        _monitor.Stop();
    }

    private void OnSnapshot(SystemSnapshot s) => ApplySnapshot(s);

    private async Task RefreshAsync()
    {
        try
        {
            var snap = await _monitor.SampleOnceAsync();
            ApplySnapshot(snap);
            RefreshHistory();
        }
        catch (Exception ex)
        {
            Logger.Error("Dashboard refresh", ex);
            StatusSummary = "Не удалось обновить показатели";
        }
    }

    private void ApplySnapshot(SystemSnapshot s)
    {
        CpuText = $"{s.CpuPercent:0}%";
        RamText = s.RamDetail ?? $"{s.RamUsedPercent:0}% занято";
        CpuTempText = string.IsNullOrWhiteSpace(s.CpuTemp) ? "н/д" : s.CpuTemp!;
        GpuTempText = string.IsNullOrWhiteSpace(s.GpuTemp) ? "н/д" : s.GpuTemp!;
        StatusSummary = s.CpuPercent > 90 || s.RamUsedPercent > 90
            ? "Высокая нагрузка — проверьте автозагрузку и фоновые задачи"
            : "Система в рабочем состоянии";
    }

    private void RefreshHistory()
    {
        RecentOperations.Clear();
        foreach (var op in _history.Recent.Take(8))
            RecentOperations.Add(op);
    }

    public void Dispose() => OnDeactivated();
}
