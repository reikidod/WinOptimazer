using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Windows.Input;
using System.Windows.Threading;
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
    private readonly PageNavigationHistory<AppPage> _navigationHistory = new();
    private readonly DispatcherTimer _dashboardTimer;
    private readonly SemaphoreSlim _dashboardRefreshGate = new(1, 1);
    private bool _dashboardRefreshing;
    private bool _isHistoryNavigation;
    private readonly DiagnosticsService _diagnostics = new();
    private readonly OperationHistoryService _history = new();
    private readonly HashSet<AppPage> _initializedPages = new();
    private SystemSnapshot? _dashboardSnapshot;
    private IReadOnlyList<DiagnosticFinding> _diagnosticFindings = Array.Empty<DiagnosticFinding>();
    private string _dashboardStatus = "Загружаю состояние компьютера…";

    public SettingsViewModel Settings { get; } = new();

    public ProcessMonitorViewModel Processes { get; } = new();

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
        {
            tweak.PropertyChanged += OnTweakPropertyChanged;
        }

        // Показатели на панели обновляются сами, пока она открыта (процессор меняется каждую секунду).
        _dashboardTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _dashboardTimer.Tick += async (_, _) => await RefreshDashboardQuietAsync();
        _dashboardTimer.Start();

        Processes.Start();

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
    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetField(ref _updateStatus, value);
    }

    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (SetField(ref _updateAvailable, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    private string? _pendingDownloadUrl;


    public ICommand ApplyCommand { get; }

    public ICommand ResetSelectionCommand { get; }

    public ICommand CreateRestorePointCommand { get; }

    public ICommand RefreshInfoCommand { get; }

    // Три взаимоисключающих флага для переключателей навигации. Снятие флага ничего не делает:
    // RadioButton сообщает «false» старой странице, когда пользователь выбирает другую.
    public bool IsDashboardPage
    {
        get => _page == AppPage.Dashboard;
        set => SelectPage(AppPage.Dashboard, value);
    }

    public bool IsTweaksPage
    {
        get => _page == AppPage.Tweaks;
        set => SelectPage(AppPage.Tweaks, value);
    }

    public bool IsSystemPage
    {
        get => _page == AppPage.System;
        set => SelectPage(AppPage.System, value);
    }

    public bool IsMaintenancePage
    {
        get => _page == AppPage.Maintenance;
        set => SelectPage(AppPage.Maintenance, value);
    }

    public bool IsDebloatPage
    {
        get => _page == AppPage.Debloat;
        set => SelectPage(AppPage.Debloat, value);
    }

    public bool IsApplicationsGroupPage
    {
        get => _page is AppPage.Apps or AppPage.Debloat or AppPage.Store;
        set
        {
            if (value) Navigate(nameof(AppPage.Apps));
        }
    }

    public bool IsAppsPage
    {
        get => _page == AppPage.Apps;
        set => SelectPage(AppPage.Apps, value);
    }

    public bool IsStorePage
    {
        get => _page == AppPage.Store;
        set => SelectPage(AppPage.Store, value);
    }

    public bool IsGamesPage
    {
        get => _page == AppPage.Games;
        set => SelectPage(AppPage.Games, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsTweakStateReady
    {
        get => _isTweakStateReady;
        private set
        {
            if (SetField(ref _isTweakStateReady, value))
                CommandManager.InvalidateRequerySuggested();
        }
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

    public bool RebootRequired
    {
        get => _rebootRequired;
        private set => SetField(ref _rebootRequired, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public int PendingCount => Tweaks.Count(t => t.IsChanged && t.IsAvailable);

    public string PendingText => PendingCount switch
    {
        0 => "Нет несохранённых изменений",
        var n => $"Изменений к применению: {n}",
    };

    public async Task InitializeAsync()
    {
        // Стартуем с лёгкой панели. Тяжёлые разделы загружаются только при первом открытии.
        await RefreshDashboardAsync();
        StatusText = "Готово";
    }

    private async Task RefreshDashboardAsync()
    {
        try
        {
            DashboardStatus = "Обновляю системные показатели…";
            DashboardSnapshot = await CollectDashboardSnapshotAsync();
            RefreshRecentOperations();
            DashboardStatus = $"Обновлено: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось обновить панель состояния", ex);
            DashboardStatus = "Не удалось получить часть показателей: " + ex.Message;
        }
    }

    private async Task<SystemSnapshot> CollectDashboardSnapshotAsync()
    {
        await _dashboardRefreshGate.WaitAsync();
        try
        {
            return await Task.Run(SystemMonitorService.Collect);
        }
        finally
        {
            _dashboardRefreshGate.Release();
        }
    }

    /// <summary>Фоновое обновление показателей без смены текста статуса «Обновляю…» (чтобы он не мигал).</summary>
    private async Task RefreshDashboardQuietAsync()
    {
        if (!IsDashboardPage || _dashboardRefreshing || IsBusy) return;

        _dashboardRefreshing = true;
        try
        {
            DashboardSnapshot = await CollectDashboardSnapshotAsync();
            DashboardStatus = $"Обновлено: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось обновить панель состояния", ex);
        }
        finally
        {
            _dashboardRefreshing = false;
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        var snapshot = DashboardSnapshot;
        if (snapshot is null)
        {
            await RefreshDashboardAsync();
            snapshot = DashboardSnapshot;
        }
        if (snapshot is null)
        {
            DashboardStatus = "Диагностика недоступна: не удалось получить системные показатели.";
            return;
        }

        DashboardStatus = "Проверяю состояние Windows…";
        DiagnosticFindings = await Task.Run(() => _diagnostics.Run(snapshot));
        RecordOperation("Диагностика", "Проверка состояния Windows", "Завершено");
        DashboardStatus = $"Диагностика завершена: найдено замечаний — {DiagnosticFindings.Count(x => x.Level != DiagnosticLevel.Info)}.";
    }

    private void RefreshRecentOperations()
    {
        RecentOperations.Clear();
        foreach (var entry in _history.GetRecent(8)) RecentOperations.Add(entry);
    }

    private void RecordOperation(string category, string operation, string result)
    {
        try
        {
            var entry = _history.Record(category, operation, result);
            RecentOperations.Insert(0, entry);
            while (RecentOperations.Count > 8) RecentOperations.RemoveAt(RecentOperations.Count - 1);
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось сохранить историю операций", ex);
        }
    }

    private void SaveProfile()
    {
        if (!IsTweakStateReady || IsBusy) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить профиль оптимизации",
            Filter = "Профиль WinOptimizer (*.json)|*.json",
            FileName = "WinOptimizer-profile.json",
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var profile = OptimizationProfileService.Create(
                Path.GetFileNameWithoutExtension(dialog.FileName),
                Tweaks.Where(t => t.IsSelected).Select(t => t.Tweak.Info.Id));
            OptimizationProfileService.Save(dialog.FileName, profile);
            StatusText = $"Профиль сохранён: {profile.Name}.";
            _dialogs.Info("Профиль сохранён",
                $"Сохранено выбранных твиков: {profile.SelectedTweakIds.Count}. Профиль не применяет изменения автоматически.",
                DialogKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Logger.Error("Не удалось сохранить профиль оптимизации", ex);
            _dialogs.Info("Ошибка профиля", ex.Message, DialogKind.Warning);
        }
    }

    private void LoadProfile()
    {
        if (!IsTweakStateReady || IsBusy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Загрузить профиль оптимизации",
            Filter = "Профиль WinOptimizer (*.json)|*.json",
            DefaultExt = ".json",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var profile = OptimizationProfileService.Load(dialog.FileName);
            var selected = new HashSet<string>(profile.SelectedTweakIds, StringComparer.OrdinalIgnoreCase);
            foreach (var tweak in Tweaks)
                tweak.IsSelected = selected.Contains(tweak.Tweak.Info.Id);

            var known = Tweaks.Select(t => t.Tweak.Info.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = selected.Count(id => !known.Contains(id));
            StatusText = $"Профиль «{profile.Name}» загружен. Изменений к применению: {PendingCount}.";
            _dialogs.Info("Профиль загружен",
                $"Выбрано твиков: {selected.Count - unknown}. Неизвестных идентификаторов: {unknown}. Изменения ещё не применены — проверьте список и нажмите «Применить».",
                DialogKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Logger.Error("Не удалось загрузить профиль оптимизации", ex);
            _dialogs.Info("Ошибка профиля", ex.Message, DialogKind.Warning);
        }
    }

    private void Navigate(string? pageName)
    {
        if (!Enum.TryParse<AppPage>(pageName, ignoreCase: true, out var page))
            return;

        // Если пользователь скрыл установку, но оставил удаление или Store,
        // кнопка группы открывает первый доступный подраздел.
        if (page == AppPage.Apps && !AppSettings.Current.IsTabVisible("Apps"))
        {
            page = AppSettings.Current.IsTabVisible("Debloat")
                ? AppPage.Debloat
                : AppSettings.Current.IsTabVisible("Store") ? AppPage.Store : AppPage.Settings;
        }

        SelectPage(page, true);
    }

    /// <summary>Переход к предыдущему открытому разделу (Mouse 4 / кнопка «Назад»).</summary>
    public bool NavigateBack() => NavigateHistory(forward: false);

    /// <summary>Возврат к разделу, из которого был выполнен переход назад (Mouse 5 / «Вперёд»).</summary>
    public bool NavigateForward() => NavigateHistory(forward: true);

    private bool NavigateHistory(bool forward)
    {
        var target = _page;
        var moved = forward
            ? _navigationHistory.TryForward(_page, IsPageVisible, out target)
            : _navigationHistory.TryBack(_page, IsPageVisible, out target);
        if (!moved) return false;

        _isHistoryNavigation = true;
        try
        {
            SelectPage(target, true);
        }
        finally
        {
            _isHistoryNavigation = false;
        }

        return true;
    }

    private static bool IsPageVisible(AppPage page) => page switch
    {
        AppPage.Tweaks => AppSettings.Current.IsTabVisible("Tweaks"),
        AppPage.Debloat => AppSettings.Current.IsTabVisible("Debloat"),
        AppPage.Apps => AppSettings.Current.IsTabVisible("Apps"),
        AppPage.Store => AppSettings.Current.IsTabVisible("Store"),
        AppPage.Games => AppSettings.Current.IsTabVisible("Games"),
        AppPage.System => AppSettings.Current.IsTabVisible("System"),
        AppPage.Maintenance => AppSettings.Current.IsTabVisible("Maintenance"),
        _ => true,
    };

    private async Task InitializePageAsync(AppPage page)
    {
        if (!_initializedPages.Add(page)) return;
        try
        {
            switch (page)
            {
                case AppPage.Tweaks:
                    await RefreshTweakStatesAsync();
                    break;
                case AppPage.System:
                    await RefreshInfoAsync();
                    break;
                case AppPage.Maintenance:
                    await Maintenance.InitializeAsync();
                    break;
                case AppPage.Debloat:
                    await Debloat.InitializeAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _initializedPages.Remove(page);
            Logger.Error($"Не удалось загрузить раздел {page}", ex);
            StatusText = $"Не удалось загрузить раздел: {page}";
        }
    }

    public string BuildInfoReport() => SystemInfoService.ToPlainText(InfoSections);

    private void SetCategory(string? category)
    {
        if (!IsTweakStateReady || IsBusy) return;
        if (!string.IsNullOrWhiteSpace(category))
            TweakCategory = category;
    }

    private void ToggleGroup(TweakViewModel? header)
    {
        if (!IsTweakStateReady || IsBusy) return;
        if (header is null || !header.IsGroupHeader) return;
        header.IsGroupExpanded = !header.IsGroupExpanded;
        OnPropertyChanged(nameof(FilteredTweaks));
    }

    private static void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }

    public bool IsSettingsPage
    {
        get => _page == AppPage.Settings;
        set => SelectPage(AppPage.Settings, value);
    }

    /// <summary>Если открытую вкладку скрыли в настройках, переходим в «Настройки».</summary>
    private void EnsurePageVisible()
    {
        if (_page != AppPage.Settings && !AppSettings.Current.IsTabVisible(_page.ToString()))
            SelectPage(AppPage.Settings, true);
    }

    private void SelectPage(AppPage page, bool selected)
    {
        if (!selected || _page == page)
        {
            return;
        }

        if (!_isHistoryNavigation)
        {
            _navigationHistory.Record(_page, page);
        }

        _page = page;
        OnPropertyChanged(nameof(IsDashboardPage));
        OnPropertyChanged(nameof(IsTweaksPage));
        OnPropertyChanged(nameof(IsSystemPage));
        Processes.IsPageVisible = IsSystemPage;
        OnPropertyChanged(nameof(IsMaintenancePage));
        OnPropertyChanged(nameof(IsDebloatPage));
        OnPropertyChanged(nameof(IsAppsPage));
        OnPropertyChanged(nameof(IsApplicationsGroupPage));
        OnPropertyChanged(nameof(IsStorePage));
        OnPropertyChanged(nameof(IsGamesPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        _ = InitializePageAsync(page);
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
        if (!IsTweakStateReady || IsBusy) return;
        foreach (var tweak in Tweaks)
        {
            tweak.IsSelected = tweak.IsApplied;
        }
    }

    private async Task ApplyAsync()
    {
        if (!IsTweakStateReady || IsBusy) return;
        var changes = Tweaks.Where(t => t.IsChanged && t.IsAvailable).ToList();
        if (changes.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (!await EnsureRestorePointAsync(changes))
            {
                StatusText = "Применение отменено";
                return;
            }

            StatusText = "Применяю изменения…";

            // Желаемое состояние фиксируем в UI-потоке, дальше работаем в фоне.
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
                    RecordOperation("Оптимизация", (apply ? "Применён твик: " : "Откатан твик: ") + vm.Title, "Успешно");
                }
                else
                {
                    failed++;
                    vm.ErrorText = $"Не удалось {(apply ? "применить" : "откатить")}: {outcome.Error}";
                    RecordOperation("Оптимизация", (apply ? "Применение: " : "Откат: ") + vm.Title, "Ошибка. Подробности записаны в журнал.");
                }
            }

            StatusText = string.Create(CultureInfo.CurrentCulture, $"Готово. Успешно: {results.Count - failed}, с ошибкой: {failed}.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Предлагает точку восстановления перед системными изменениями. False — пользователь отменил применение.</summary>
    private async Task<bool> EnsureRestorePointAsync(IReadOnlyList<TweakViewModel> changes)
    {
        var system = changes.Where(c => c.RequiresRestorePoint).ToList();
        if (system.Count == 0 || !AppSettings.Current.AskRestorePoint)
        {
            return true;
        }

        var list = string.Join(Environment.NewLine, system.Select(s => "• " + s.Title));
        var choice = _dialogs.Ask(
            "Сохранить снимок системы?",
            $"Будут изменены параметры Windows:{Environment.NewLine}{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}" +
            "Снимок позволит откатить изменения через «Параметры → Система → Восстановление», если что-то пойдёт не так.",
            primary: "Создать снимок",
            secondary: "Без снимка",
            cancel: "Не применять",
            kind: DialogKind.RestorePoint,
            subtitle: "Рекомендуется перед системными твиками");

        switch (choice)
        {
            case DialogChoice.Cancel:
                return false; // не применять твики
            case DialogChoice.Secondary:
                return true;  // применить без точки
        }

        StatusText = "Создаю точку восстановления…";
        var result = await _restorePoints.CreateAsync(NewRestorePointName());
        if (result.Success)
        {
            StatusText = "Точка восстановления создана";
            return true;
        }

        var next = _dialogs.Ask(
            "Снимок не создан",
            result.Message + Environment.NewLine + Environment.NewLine +
            "Частые причины: защита системы выключена или точка уже создавалась недавно. Продолжить без снимка?",
            primary: "Продолжить без снимка",
            kind: DialogKind.Warning,
            subtitle: "Восстановление недоступно");
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
            primary: "Создать снимок",
            secondary: "Отмена",
            cancel: "",
            kind: DialogKind.RestorePoint,
            subtitle: "Контрольная точка Windows");
        if (confirm != DialogChoice.Primary)
            return;

        IsBusy = true;
        try
        {
            StatusText = "Создаю точку восстановления…";
            var result = await _restorePoints.CreateAsync(NewRestorePointName());
            StatusText = result.Message;
            if (result.Success)
            {
                _dialogs.Info("Снимок создан",
                    result.Message + Environment.NewLine + Environment.NewLine +
                    "Откат: Параметры → Система → Восстановление → Запуск восстановления системы.",
                    DialogKind.Success);
            }
            else
            {
                _dialogs.Info("Не удалось создать снимок", result.Message, DialogKind.Warning);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshInfoAsync()
    {
        IsInfoLoading = true;
        try
        {
            var sections = await _systemInfo.CollectAsync();
            InfoSections.Clear();
            foreach (var section in sections)
            {
                InfoSections.Add(section);
            }
        }
        finally
        {
            IsInfoLoading = false;
            OnPropertyChanged(nameof(HasNoInfo));
        }
    }

    private async Task RefreshTweakStatesAsync()
    {
        IsTweakStateReady = false;
        StatusText = "Считываю текущее состояние твиков…";
        try
        {
            var snapshot = Tweaks.ToList();
            var states = await Task.Run(() => snapshot.Select(t => (Vm: t, State: ReadState(t.Tweak))).ToList());
            foreach (var (vm, state) in states)
            {
                vm.SetState(state.Applied, state.Error);
            }
        }
        finally
        {
            IsTweakStateReady = true;
            if (StatusText == "Считываю текущее состояние твиков…")
                StatusText = "Готово";
        }
    }

    private static (bool? Applied, string? Error) ReadState(ITweak tweak)
    {
        try
        {
            return (tweak.IsApplied(), null);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            Logger.Error($"Не удалось прочитать состояние {tweak.Info.Id}", ex);
            return (null, $"Не удалось определить состояние: {ex.Message}");
        }
    }

    private static (bool Success, string? Error) RunTweak(ITweak tweak, bool apply)
    {
        try
        {
            if (apply)
            {
                tweak.Apply();
            }
            else
            {
                tweak.Revert();
            }

            return (true, null);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            Logger.Error($"Ошибка при {(apply ? "применении" : "откате")} {tweak.Info.Id}", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>Ожидаемые отказы окружения: права, реестр, внешние утилиты. Остальное — баги и должны падать громко.</summary>
    private static bool IsExpectedFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException
            or InvalidDataException or NotSupportedException or TimeoutException or Win32Exception
            or ArgumentException or FormatException;

    private static string NewRestorePointName() =>
        string.Create(CultureInfo.InvariantCulture, $"WinOptimizer {DateTime.Now:yyyy-MM-dd HH:mm}");


    public async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var result = await _updates.CheckAsync();
            UpdateStatus = result.Message;
            UpdateAvailable = result.UpdateAvailable && result.DownloadUrl is not null;
            _pendingDownloadUrl = result.DownloadUrl;
            if (result.UpdateAvailable && result.DownloadUrl is not null)
            {
                if (AskInstallUpdate(result))
                    await ApplyUpdateAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Startup update check", ex);
            UpdateStatus = "Проверка обновлений не удалась";
        }
    }

    /// <summary>Окно «доступно обновление»: версия, краткий список изменений из описания релиза, выбор «Установить / Позже».</summary>
    private bool AskInstallUpdate(UpdateCheckResult result)
    {
        var notes = ReleaseNotesFormatter.Summarize(result.ReleaseNotes);
        var message = notes.Length == 0
            ? result.Message
            : $"{result.Message}\n\nЧто изменилось:\n{notes}";
        return _dialogs.Ask("Обновление WinOptimizer", message, "Установить", cancel: "Позже",
            kind: DialogKind.Default, subtitle: "Доступна новая версия") == DialogChoice.Primary;
    }

    private async Task CheckUpdateAsync()
    {
        IsBusy = true;
        try
        {
            UpdateStatus = "Проверяю GitHub…";
            var result = await _updates.CheckAsync();
            UpdateStatus = result.Message;
            UpdateAvailable = result.UpdateAvailable && result.DownloadUrl is not null;
            _pendingDownloadUrl = result.DownloadUrl;
            if (result.UpdateAvailable && result.DownloadUrl is not null)
            {
                if (AskInstallUpdate(result))
                    await ApplyUpdateAsync();
            }
            else
            {
                _dialogs.Info("Обновление", result.Message, DialogKind.Success);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_pendingDownloadUrl is null) return;
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => UpdateStatus = s);
            var (ok, msg) = await _updates.DownloadAndInstallAsync(_pendingDownloadUrl, progress);
            UpdateStatus = msg;
            if (!ok)
                _dialogs.Info("Обновление", msg, DialogKind.Warning);
            else
            {
                try { System.Windows.Application.Current?.Shutdown(); } catch { /* ignore */ }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReportUnexpected(Exception ex)
    {
        StatusText = "Произошла непредвиденная ошибка";
        _dialogs.Info("Непредвиденная ошибка", $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Подробности записаны в {AppPaths.LogFile}");
    }
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
