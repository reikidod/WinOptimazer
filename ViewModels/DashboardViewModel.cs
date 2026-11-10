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
    private string _cpuSubtitle = "загрузка";
    private string _ramText = "—";
    private string _cpuTempText = "—";
    private string _gpuTempText = "—";
    private string _statusSummary = "Загрузка…";
    private int _monitorIntervalSeconds = 3;

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
        SetIntervalCommand = new RelayCommand(p => SetInterval(p));

        Profiles = new ObservableCollection<OptimizationProfile>(_profiles.BuiltIn);
        MonitorIntervalSeconds = AppSettings.Current.MonitorIntervalSeconds;
        RefreshHistory();
        AppSettings.Changed += OnSettingsChanged;
    }

    public ObservableCollection<OptimizationProfile> Profiles { get; }

    public ObservableCollection<OperationRecord> RecentOperations { get; } = new();

    public IReadOnlyList<int> IntervalOptions => AppSettings.MonitorIntervalOptions;

    public string CpuText { get => _cpuText; private set => SetField(ref _cpuText, value); }
    public string CpuSubtitle { get => _cpuSubtitle; private set => SetField(ref _cpuSubtitle, value); }
    public string RamText { get => _ramText; private set => SetField(ref _ramText, value); }
    public string CpuTempText { get => _cpuTempText; private set => SetField(ref _cpuTempText, value); }
    public string GpuTempText { get => _gpuTempText; private set => SetField(ref _gpuTempText, value); }
    public string StatusSummary { get => _statusSummary; private set => SetField(ref _statusSummary, value); }

    public int MonitorIntervalSeconds
    {
        get => _monitorIntervalSeconds;
        private set
        {
            if (!SetField(ref _monitorIntervalSeconds, value)) return;
            OnPropertyChanged(nameof(IsInterval2));
            OnPropertyChanged(nameof(IsInterval3));
            OnPropertyChanged(nameof(IsInterval5));
            OnPropertyChanged(nameof(IsInterval10));
            OnPropertyChanged(nameof(IntervalHint));
        }
    }

    public bool IsInterval2 => MonitorIntervalSeconds == 2;
    public bool IsInterval3 => MonitorIntervalSeconds == 3;
    public bool IsInterval5 => MonitorIntervalSeconds == 5;
    public bool IsInterval10 => MonitorIntervalSeconds == 10;

    public string IntervalHint => $"Обновление каждые {MonitorIntervalSeconds} с";

    public ICommand OpenTweaksCommand { get; }
    public ICommand OpenMaintenanceCommand { get; }
    public ICommand OpenGamesCommand { get; }
    public ICommand OpenSystemCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SetIntervalCommand { get; }

    public void OnActivated()
    {
        if (_active) return;
        _active = true;
        _monitor.Updated += OnSnapshot;
        RestartMonitor();
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
        if (!_active) return;
        _active = false;
        _monitor.Updated -= OnSnapshot;
        _monitor.Stop();
    }

    private void OnSettingsChanged()
    {
        var sec = AppSettings.Current.MonitorIntervalSeconds;
        if (sec == MonitorIntervalSeconds) return;
        MonitorIntervalSeconds = sec;
        if (_active) RestartMonitor();
    }

    private void SetInterval(object? parameter)
    {
        var sec = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, out var v) => v,
            _ => MonitorIntervalSeconds,
        };
        if (!AppSettings.MonitorIntervalOptions.Contains(sec)) return;
        if (sec == MonitorIntervalSeconds) return;

        AppSettings.Current.MonitorIntervalSeconds = sec;
        AppSettings.Current.Save();
        MonitorIntervalSeconds = sec;
        if (_active) RestartMonitor();
    }

    private void RestartMonitor()
    {
        _monitor.Stop();
        _monitor.Start(TimeSpan.FromSeconds(Math.Max(2, MonitorIntervalSeconds)));
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
        CpuSubtitle = $"{Environment.ProcessorCount} логических · загрузка";
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

    public void Dispose()
    {
        AppSettings.Changed -= OnSettingsChanged;
        OnDeactivated();
    }
}
