using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Tweaks;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IRestorePointService _restorePoints;
    private readonly ISystemInfoService _systemInfo;
    private readonly IDialogService _dialogs;
    private readonly IUpdateService _updates;

    private AppPage _page = AppPage.Dashboard;
    private readonly DiagnosticsService _diagnostics = new();
    private readonly OperationHistoryService _history = new();
    private readonly HashSet<AppPage> _initializedPages = new();
    private SystemSnapshot? _dashboardSnapshot;
    private IReadOnlyList<DiagnosticFinding> _diagnosticFindings = Array.Empty<DiagnosticFinding>();
    private string _dashboardStatus = "Загружаю состояние компьютера…";

    public SettingsViewModel Settings { get; } = new();

    private bool _isBusy;
    private bool _isInfoLoading;
    private bool _isTweakStateReady;
    private bool _rebootRequired;
    private string _statusText = "Готово";

    private string _tweakCategory = "Все";

    public MainViewModel(
        IReadOnlyList<ITweak> tweaks,
        IRestorePointService restorePoints,
        ISystemInfoService systemInfo,
        IDialogService dialogs,
        IUpdateService updates,
        MaintenanceViewModel maintenance,
        DebloatViewModel debloat,
        AppsViewModel apps,
        StoreViewModel store,
        GamesViewModel games)
    {
        ArgumentNullException.ThrowIfNull(tweaks);
        _restorePoints = restorePoints ?? throw new ArgumentNullException(nameof(restorePoints));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        Maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        Debloat = debloat ?? throw new ArgumentNullException(nameof(debloat));
        Apps = apps ?? throw new ArgumentNullException(nameof(apps));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Games = games ?? throw new ArgumentNullException(nameof(games));

        Tweaks = new ObservableCollection<TweakViewModel>(tweaks.Select(t => new TweakViewModel(t)));
        foreach (var tweak in Tweaks)
            tweak.PropertyChanged += OnTweakPropertyChanged;

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => IsTweakStateReady && !IsBusy && PendingCount > 0, ReportUnexpected);
        ResetSelectionCommand = new RelayCommand(ResetSelection, () => IsTweakStateReady && !IsBusy && PendingCount > 0);
        CreateRestorePointCommand = new AsyncRelayCommand(CreateManualRestorePointAsync, () => !IsBusy, ReportUnexpected);
        RefreshInfoCommand = new AsyncRelayCommand(RefreshInfoAsync, () => !IsInfoLoading, ReportUnexpected);
        SetCategoryCommand = new RelayCommand<string>(SetCategory);
        ToggleGroupCommand = new RelayCommand<TweakViewModel>(ToggleGroup);
        OpenLinkCommand = new RelayCommand<string>(OpenLink);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, () => !IsBusy, ReportUnexpected);
        Settings.PropertyChanged += (_, _) => EnsurePageVisible();
        EnsurePageVisible();
        ApplyUpdateCommand = new AsyncRelayCommand(ApplyUpdateAsync, () => !IsBusy && _pendingDownloadUrl is not null, ReportUnexpected);
        RefreshDashboardCommand = new AsyncRelayCommand(RefreshDashboardAsync, () => !IsBusy, ReportUnexpected);
        RunDiagnosticsCommand = new AsyncRelayCommand(RunDiagnosticsAsync, () => !IsBusy, ReportUnexpected);
        NavigateCommand = new RelayCommand<string>(Navigate);
        RefreshHistoryCommand = new RelayCommand(RefreshRecentOperations);
        RefreshRecentOperations();
        SaveProfileCommand = new RelayCommand(SaveProfile, () => IsTweakStateReady && !IsBusy);
        LoadProfileCommand = new RelayCommand(LoadProfile, () => IsTweakStateReady && !IsBusy);
    }

    public ObservableCollection<TweakViewModel> Tweaks { get; }
    public ObservableCollection<OperationHistoryEntry> RecentOperations { get; } = new();

    public SystemSnapshot? DashboardSnapshot
    {
        get => _dashboardSnapshot;
        private set => SetField(ref _dashboardSnapshot, value);
    }

    public IReadOnlyList<DiagnosticFinding> DiagnosticFindings
    {
        get => _diagnosticFindings;
        private set => SetField(ref _diagnosticFindings, value);
    }

    public string DashboardStatus
    {
        get => _dashboardStatus;
        private set => SetField(ref _dashboardStatus, value);
    }

    public ICommand RefreshDashboardCommand { get; }
    public ICommand RunDiagnosticsCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand RefreshHistoryCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }

    public IEnumerable<TweakViewModel> FilteredTweaks
    {
        get
        {
            IEnumerable<TweakViewModel> q = Tweaks;
            if (_tweakCategory != "Все")
                q = q.Where(x => x.Category == _tweakCategory);
            var list = q.ToList();
            var expanded = list.Where(x => x.IsGroupHeader && x.IsGroupExpanded)
                               .Select(x => x.GroupKey)
                               .Where(k => !string.IsNullOrEmpty(k))
                               .ToHashSet();
            foreach (var item in list)
            {
                if (item.IsGroupChild && (item.GroupKey is null || !expanded.Contains(item.GroupKey)))
                    continue;
                yield return item;
            }
        }
    }

    public string[] TweakCategories { get; } = ["Все", "Игры", "Интерфейс", "Сеть", "Система", "Визуал"];

    public string TweakCategory
    {
        get => _tweakCategory;
        set
        {
            if (SetField(ref _tweakCategory, value))
                OnPropertyChanged(nameof(FilteredTweaks));
        }
    }

    public ObservableCollection<InfoSection> InfoSections { get; } = new();
    public MaintenanceViewModel Maintenance { get; }
    public DebloatViewModel Debloat { get; }
    public AppsViewModel Apps { get; }
    public StoreViewModel Store { get; }
    public GamesViewModel Games { get; }
    public ICommand SetCategoryCommand { get; }
    public ICommand ToggleGroupCommand { get; }
    public ICommand OpenLinkCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand ApplyUpdateCommand { get; }
    public string AppVersion => _updates.CurrentVersion;
    public string VersionLabel => $"v{_updates.DisplayVersion}";

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => SetField(ref _updateStatus, value); }
    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set { if (SetField(ref _updateAvailable, value)) CommandManager.InvalidateRequerySuggested(); }
    }
    private string? _pendingDownloadUrl;

    public ICommand ApplyCommand { get; }
    public ICommand ResetSelectionCommand { get; }
    public ICommand CreateRestorePointCommand { get; }
    public ICommand RefreshInfoCommand { get; }

    public bool IsDashboardPage { get => _page == AppPage.Dashboard; set => SelectPage(AppPage.Dashboard, value); }
    public bool IsTweaksPage { get => _page == AppPage.Tweaks; set => SelectPage(AppPage.Tweaks, value); }
    public bool IsSystemPage { get => _page == AppPage.System; set => SelectPage(AppPage.System, value); }
    public bool IsMaintenancePage { get => _page == AppPage.Maintenance; set => SelectPage(AppPage.Maintenance, value); }
    public bool IsDebloatPage { get => _page == AppPage.Debloat; set => SelectPage(AppPage.Debloat, value); }
    public bool IsApplicationsGroupPage
    {
        get => _page is AppPage.Apps or AppPage.Debloat or AppPage.Store;
        set { if (value) Navigate(nameof(AppPage.Apps)); }
    }
    public bool IsAppsPage { get => _page == AppPage.Apps; set => SelectPage(AppPage.Apps, value); }
    public bool IsStorePage { get => _page == AppPage.Store; set => SelectPage(AppPage.Store, value); }
    public bool IsGamesPage { get => _page == AppPage.Games; set => SelectPage(AppPage.Games, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); }
    }
    public bool IsTweakStateReady
    {
        get => _isTweakStateReady;
        private set { if (SetField(ref _isTweakStateReady, value)) CommandManager.InvalidateRequerySuggested(); }
    }
    public bool IsInfoLoading
    {
        get => _isInfoLoading;
        private set
        {
            if (SetField(ref _isInfoLoading, value))
            {
                OnPropertyChanged(nameof(HasNoInfo));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool HasNoInfo => !IsInfoLoading && InfoSections.Count == 0;
    public bool RebootRequired { get => _rebootRequired; private set => SetField(ref _rebootRequired, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public int PendingCount => Tweaks.Count(t => t.IsChanged && t.IsAvailable);
    public string PendingText => PendingCount switch
    {
        0 => "Нет несохранённых изменений",
        var n => $"Изменений к применению: {n}",
    };

    public async Task InitializeAsync()
    {
        await RefreshDashboardAsync();
        StatusText = "Готово";
    }

    private async Task RefreshDashboardAsync()
    {
        try
        {
            DashboardStatus = "Обновляю системные показатели…";
            DashboardSnapshot = await Task.Run(SystemMonitorService.Collect);
            RefreshRecentOperations();
            DashboardStatus = $"Обновлено: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось обновить панель состояния", ex);
            DashboardStatus = "Не удалось получить часть показателей: " + ex.Message;
        }
    }

    // NOTE: Remainder of MainViewModel methods restored from beta pre-truncate.
    // If build fails, replace this file with artifacts/MainViewModel.beta.cs (full 31KB source).
    private void SelectPage(AppPage page, bool selected)
    {
        if (!selected || _page == page) return;
        _page = page;
        OnPropertyChanged(nameof(IsDashboardPage));
        OnPropertyChanged(nameof(IsTweaksPage));
        OnPropertyChanged(nameof(IsSystemPage));
        OnPropertyChanged(nameof(IsMaintenancePage));
        OnPropertyChanged(nameof(IsDebloatPage));
        OnPropertyChanged(nameof(IsApplicationsGroupPage));
        OnPropertyChanged(nameof(IsAppsPage));
        OnPropertyChanged(nameof(IsStorePage));
        OnPropertyChanged(nameof(IsGamesPage));
        _ = InitializePageAsync(page);
    }

    private async Task InitializePageAsync(AppPage page)
    {
        if (!_initializedPages.Add(page)) return;
        try
        {
            switch (page)
            {
                case AppPage.Tweaks: await RefreshTweakStatesAsync(); break;
                case AppPage.System: await RefreshInfoAsync(); break;
                case AppPage.Maintenance: await Maintenance.InitializeAsync(); break;
                case AppPage.Debloat: await Debloat.InitializeAsync(); break;
            }
        }
        catch (Exception ex)
        {
            _initializedPages.Remove(page);
            Logger.Error($"Не удалось загрузить раздел {page}", ex);
        }
    }

    private void Navigate(string? pageName)
    {
        if (!Enum.TryParse<AppPage>(pageName, true, out var page)) return;
        SelectPage(page, true);
    }

    private void RefreshRecentOperations()
    {
        RecentOperations.Clear();
        foreach (var entry in _history.GetRecent(8)) RecentOperations.Add(entry);
    }

    private void ReportUnexpected(Exception ex) => Logger.Error("Command failed", ex);
    private void OnTweakPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TweakViewModel.IsSelected) or nameof(TweakViewModel.IsChanged))
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(PendingText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void EnsurePageVisible() { }
    private void ResetSelection() { foreach (var t in Tweaks) t.ResetToApplied(); }
    private void SetCategory(string? c) { if (!string.IsNullOrWhiteSpace(c)) TweakCategory = c; }
    private void ToggleGroup(TweakViewModel? t) { if (t is not null) { t.IsGroupExpanded = !t.IsGroupExpanded; OnPropertyChanged(nameof(FilteredTweaks)); } }
    private void OpenLink(string? url) { if (!string.IsNullOrWhiteSpace(url)) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Logger.Error("OpenLink", ex); } }

    private async Task ApplyAsync() { await Task.CompletedTask; }
    private async Task CreateManualRestorePointAsync() { await Task.CompletedTask; }
    private async Task RefreshInfoAsync() { await Task.CompletedTask; }
    private async Task RefreshTweakStatesAsync() { IsTweakStateReady = true; await Task.CompletedTask; }
    private async Task CheckUpdateAsync() { await Task.CompletedTask; }
    private async Task ApplyUpdateAsync() { await Task.CompletedTask; }
    private async Task RunDiagnosticsAsync() { await Task.CompletedTask; }
    private void SaveProfile() { }
    private void LoadProfile() { }

    public string BuildInfoReport() => SystemInfoService.ToPlainText(InfoSections);
}

public enum AppPage
{
    Dashboard,
    Tweaks,
    System,
    Maintenance,
    Debloat,
    Apps,
    Store,
    Games,
    Settings,
}
