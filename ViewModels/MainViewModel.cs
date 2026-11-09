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
    private readonly IOperationHistoryService _history;
    private readonly IOptimizationProfileService _profiles;
    private readonly ISystemMonitorService _monitor;

    private AppPage _page = AppPage.Home;

    public SettingsViewModel Settings { get; } = new();

    private bool _isBusy;
    private bool _isInfoLoading;
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
        GamesViewModel games,
        IOperationHistoryService history,
        IOptimizationProfileService profiles,
        ISystemMonitorService monitor)
    {
        ArgumentNullException.ThrowIfNull(tweaks);
        _restorePoints = restorePoints ?? throw new ArgumentNullException(nameof(restorePoints));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        Maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        Debloat = debloat ?? throw new ArgumentNullException(nameof(debloat));
        Apps = apps ?? throw new ArgumentNullException(nameof(apps));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Games = games ?? throw new ArgumentNullException(nameof(games));
        Dashboard = new DashboardViewModel(_monitor, _history, _profiles, page => SelectPage(page, true));

        Tweaks = new ObservableCollection<TweakViewModel>(tweaks.Select(t => new TweakViewModel(t)));
        foreach (var tweak in Tweaks)
            tweak.PropertyChanged += OnTweakPropertyChanged;

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && PendingCount > 0, ReportUnexpected);
        ResetSelectionCommand = new RelayCommand(ResetSelection, () => !IsBusy && PendingCount > 0);
        CreateRestorePointCommand = new AsyncRelayCommand(CreateManualRestorePointAsync, () => !IsBusy, ReportUnexpected);
        RefreshInfoCommand = new AsyncRelayCommand(RefreshInfoAsync, () => !IsInfoLoading, ReportUnexpected);
        SetCategoryCommand = new RelayCommand<string>(SetCategory);
        ToggleGroupCommand = new RelayCommand<TweakViewModel>(ToggleGroup);
        OpenLinkCommand = new RelayCommand<string>(OpenLink);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, () => !IsBusy, ReportUnexpected);
        Settings.PropertyChanged += (_, _) => EnsurePageVisible();
        EnsurePageVisible();
        ApplyUpdateCommand = new AsyncRelayCommand(ApplyUpdateAsync, () => !IsBusy && _pendingDownloadUrl is not null, ReportUnexpected);
    }

    public ObservableCollection<TweakViewModel> Tweaks { get; }

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
    public DashboardViewModel Dashboard { get; }
    public IReadOnlyList<OptimizationProfile> BuiltInProfiles => _profiles.BuiltIn;

    public ICommand SetCategoryCommand { get; }
    public ICommand ToggleGroupCommand { get; }
    public ICommand OpenLinkCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand ApplyUpdateCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ResetSelectionCommand { get; }
    public ICommand CreateRestorePointCommand { get; }
    public ICommand RefreshInfoCommand { get; }

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

    public bool IsHomePage { get => _page == AppPage.Home; set => SelectPage(AppPage.Home, value); }
    public bool IsTweaksPage { get => _page == AppPage.Tweaks; set => SelectPage(AppPage.Tweaks, value); }
    public bool IsSystemPage { get => _page == AppPage.System; set => SelectPage(AppPage.System, value); }
    public bool IsMaintenancePage { get => _page == AppPage.Maintenance; set => SelectPage(AppPage.Maintenance, value); }
    public bool IsDebloatPage { get => _page == AppPage.Debloat; set => SelectPage(AppPage.Debloat, value); }
    public bool IsAppsPage { get => _page == AppPage.Apps; set => SelectPage(AppPage.Apps, value); }
    public bool IsStorePage { get => _page == AppPage.Store; set => SelectPage(AppPage.Store, value); }
    public bool IsGamesPage { get => _page == AppPage.Games; set => SelectPage(AppPage.Games, value); }
    public bool IsSettingsPage { get => _page == AppPage.Settings; set => SelectPage(AppPage.Settings, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); }
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
        StatusText = "Проверяю текущее состояние…";
        var info = RefreshInfoAsync();
        var maintenance = Maintenance.InitializeAsync();
        var debloat = Debloat.InitializeAsync();
        try
        {
            await RefreshTweakStatesAsync();
            StatusText = "Готово";
            if (_page == AppPage.Home)
                Dashboard.OnActivated();
        }
        finally
        {
            await Task.WhenAll(info, maintenance, debloat);
        }
    }

    public string BuildInfoReport() => SystemInfoService.ToPlainText(InfoSections);

    private void SetCategory(string? category)
    {
        if (!string.IsNullOrWhiteSpace(category))
            TweakCategory = category;
    }

    private void ToggleGroup(TweakViewModel? header)
    {
        if (header is null || !header.IsGroupHeader) return;
        header.IsGroupExpanded = !header.IsGroupExpanded;
        OnPropertyChanged(nameof(FilteredTweaks));
    }

    private static void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private void EnsurePageVisible()
    {
        if (_page != AppPage.Settings && _page != AppPage.Home && !AppSettings.Current.IsTabVisible(_page.ToString()))
            SelectPage(AppPage.Settings, true);
    }

    private void SelectPage(AppPage page, bool selected)
    {
        if (!selected || _page == page) return;
        var previous = _page;
        _page = page;
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsTweaksPage));
        OnPropertyChanged(nameof(IsSystemPage));
        OnPropertyChanged(nameof(IsMaintenancePage));
        OnPropertyChanged(nameof(IsDebloatPage));
        OnPropertyChanged(nameof(IsAppsPage));
        OnPropertyChanged(nameof(IsStorePage));
        OnPropertyChanged(nameof(IsGamesPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        if (previous == AppPage.Home) Dashboard.OnDeactivated();
        if (page == AppPage.Home) Dashboard.OnActivated();
    }

    private void OnTweakPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TweakViewModel.IsGroupExpanded))
            OnPropertyChanged(nameof(FilteredTweaks));
        if (e.PropertyName is nameof(TweakViewModel.IsChanged) or nameof(TweakViewModel.IsAvailable))
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(PendingText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ResetSelection()
    {
        foreach (var tweak in Tweaks)
            tweak.IsSelected = tweak.IsApplied;
    }

    private async Task ApplyAsync()
    {
        var changes = Tweaks.Where(t => t.IsChanged && t.IsAvailable).ToList();
        if (changes.Count == 0) return;

        IsBusy = true;
        try
        {
            if (!await EnsureRestorePointAsync(changes))
            {
                StatusText = "Применение отменено";
                return;
            }

            StatusText = "Применяю изменения…";
            var work = changes.Select(t => (Vm: t, Apply: t.IsSelected)).ToList();
            var results = await Task.Run(() => work.Select(w => (w.Vm, w.Apply, Outcome: RunTweak(w.Vm.Tweak, w.Apply))).ToList());
            await RefreshTweakStatesAsync();

            var failed = 0;
            foreach (var (vm, apply, outcome) in results)
            {
                if (outcome.Success)
                {
                    RebootRequired |= vm.RequiresReboot;
                    Logger.Info($"{(apply ? "Применён" : "Откатан")}: {vm.Title}");
                }
                else
                {
                    failed++;
                    vm.ErrorText = $"Не удалось {(apply ? "применить" : "откатить")}: {outcome.Error}";
                }
            }

            StatusText = string.Create(CultureInfo.CurrentCulture, $"Готово. Успешно: {results.Count - failed}, с ошибкой: {failed}.");
            _history.Add(
                OperationKind.TweakApply,
                $"Твики: успешно {results.Count - failed}, ошибок {failed}",
                failed == 0 ? OperationOutcome.Success : failed == results.Count ? OperationOutcome.Failed : OperationOutcome.Partial,
                string.Join(", ", results.Select(r => r.Vm.Title).Take(12)));
        }
        finally { IsBusy = false; }
    }

    private async Task<bool> EnsureRestorePointAsync(IReadOnlyList<TweakViewModel> changes)
    {
        var system = changes.Where(c => c.RequiresRestorePoint).ToList();
        if (system.Count == 0 || !AppSettings.Current.AskRestorePoint) return true;

        var list = string.Join(Environment.NewLine, system.Select(s => "• " + s.Title));
        var choice = _dialogs.Ask(
            "Сохранить снимок системы?",
            $"Будут изменены параметры Windows:{Environment.NewLine}{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}" +
            "Снимок позволит откатить изменения через «Параметры → Система → Восстановление», если что-то пойдёт не так.",
            primary: "Создать снимок", secondary: "Без снимка", cancel: "Не применять",
            kind: DialogKind.RestorePoint, subtitle: "Рекомендуется перед системными твиками");

        switch (choice)
        {
            case DialogChoice.Cancel: return false;
            case DialogChoice.Secondary: return true;
        }

        StatusText = "Создаю точку восстановления…";
        var result = await _restorePoints.CreateAsync(NewRestorePointName());
        if (result.Success)
        {
            StatusText = "Точка восстановления создана";
            _history.Add(OperationKind.RestorePoint, "Точка восстановления", OperationOutcome.Success, result.Message);
            return true;
        }

        var next = _dialogs.Ask(
            "Снимок не создан", result.Message + Environment.NewLine + Environment.NewLine +
            "Частые причины: защита системы выключена или точка уже создавалась недавно. Продолжить без снимка?",
            primary: "Продолжить без снимка", kind: DialogKind.Warning, subtitle: "Восстановление недоступно");
        return next == DialogChoice.Primary;
    }

    private async Task CreateManualRestorePointAsync()
    {
        var confirm = _dialogs.Ask(
            "Создать точку восстановления?",
            $"Будет сохранён снимок текущей конфигурации Windows.{Environment.NewLine}{Environment.NewLine}" +
            $"• Полезно перед твиками, удалением приложений и установкой служб{Environment.NewLine}" +
            $"• Не заменяет полный бэкап файлов{Environment.NewLine}" +
            "• Нужны права администратора и включённая «Защита системы»",
            primary: "Создать снимок", secondary: "Отмена", cancel: "",
            kind: DialogKind.RestorePoint, subtitle: "Контрольная точка Windows");
        if (confirm != DialogChoice.Primary) return;

        IsBusy = true;
        try
        {
            StatusText = "Создаю точку восстановления…";
            var result = await _restorePoints.CreateAsync(NewRestorePointName());
            StatusText = result.Message;
            _history.Add(OperationKind.RestorePoint, "Ручная точка восстановления",
                result.Success ? OperationOutcome.Success : OperationOutcome.Failed, result.Message);
            if (result.Success)
                _dialogs.Info("Снимок создан", result.Message + Environment.NewLine + Environment.NewLine +
                    "Откат: Параметры → Система → Восстановление → Запуск восстановления системы.", DialogKind.Success);
            else
                _dialogs.Info("Не удалось создать снимок", result.Message, DialogKind.Warning);
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshInfoAsync()
    {
        IsInfoLoading = true;
        try
        {
            var sections = await _systemInfo.CollectAsync();
            InfoSections.Clear();
            foreach (var section in sections) InfoSections.Add(section);
        }
        finally
        {
            IsInfoLoading = false;
            OnPropertyChanged(nameof(HasNoInfo));
        }
    }

    private async Task RefreshTweakStatesAsync()
    {
        var snapshot = Tweaks.ToList();
        var states = await Task.Run(() => snapshot.Select(t => (Vm: t, State: ReadState(t.Tweak))).ToList());
        foreach (var (vm, state) in states)
            vm.SetState(state.Applied, state.Error);
    }

    private static (bool? Applied, string? Error) ReadState(ITweak tweak)
    {
        try { return (tweak.IsApplied(), null); }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            Logger.Error($"Не удалось прочитать состояние {tweak.Info.Id}", ex);
            return (null, $"Не удалось определить: {ex.Message}");
        }
    }

    private static (bool Success, string? Error) RunTweak(ITweak tweak, bool apply)
    {
        try
        {
            if (apply) tweak.Apply(); else tweak.Revert();
            return (true, null);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            Logger.Error($"Ошибка твика {tweak.Info.Id}", ex);
            return (false, ex.Message);
        }
    }

    private static bool IsExpectedFailure(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException or InvalidOperationException
            or System.IO.IOException or System.ComponentModel.Win32Exception
            or System.Runtime.InteropServices.COMException or ArgumentException;

    private static string NewRestorePointName() =>
        string.Create(CultureInfo.InvariantCulture, $"WinOptimizer {DateTime.Now:yyyy-MM-dd HH:mm}");

    public async Task CheckForUpdatesOnStartupAsync()
    {
        try { await CheckUpdateAsync(silent: true); }
        catch (Exception ex) { Logger.Error("Проверка обновлений при запуске", ex); }
    }

    private async Task CheckUpdateAsync() => await CheckUpdateAsync(silent: false);

    private async Task CheckUpdateAsync(bool silent)
    {
        UpdateStatus = "Проверяю обновления…";
        try
        {
            var result = await _updates.CheckAsync();
            UpdateAvailable = result.UpdateAvailable;
            _pendingDownloadUrl = result.DownloadUrl;
            UpdateStatus = result.Message;
            if (!silent && result.UpdateAvailable)
                _dialogs.Info("Доступно обновление", result.Message, DialogKind.Info);
            else if (!silent)
                _dialogs.Info("Обновления", result.Message, DialogKind.Info);
        }
        catch (Exception ex)
        {
            UpdateStatus = "Ошибка проверки обновлений";
            Logger.Error("Проверка обновлений", ex);
            if (!silent)
                _dialogs.Info("Обновления", ex.Message, DialogKind.Warning);
        }
        finally { CommandManager.InvalidateRequerySuggested(); }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_pendingDownloadUrl is null) return;
        IsBusy = true;
        try
        {
            UpdateStatus = "Скачиваю обновление…";
            var (ok, msg) = await _updates.DownloadAndInstallAsync(_pendingDownloadUrl, p => UpdateStatus = p);
            UpdateStatus = msg;
            if (!ok) _dialogs.Info("Обновление", msg, DialogKind.Warning);
            else try { System.Windows.Application.Current?.Shutdown(); } catch { }
        }
        finally { IsBusy = false; }
    }

    private void ReportUnexpected(Exception ex)
    {
        StatusText = "Произошла непредвиденная ошибка";
        _dialogs.Info("Непредвиденная ошибка", $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Подробности записаны в {AppPaths.LogFile}");
    }
}

public enum AppPage
{
    Home,
    Tweaks,
    System,
    Maintenance,
    Debloat,
    Apps,
    Store,
    Games,
    Settings,
}
