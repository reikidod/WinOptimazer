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

    private AppPage _page = AppPage.Tweaks;
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

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && PendingCount > 0, ReportUnexpected);
        ResetSelectionCommand = new RelayCommand(ResetSelection, () => !IsBusy && PendingCount > 0);
        CreateRestorePointCommand = new AsyncRelayCommand(CreateManualRestorePointAsync, () => !IsBusy, ReportUnexpected);
        RefreshInfoCommand = new AsyncRelayCommand(RefreshInfoAsync, () => !IsInfoLoading, ReportUnexpected);
        SetCategoryCommand = new RelayCommand<string>(SetCategory);
        ToggleGroupCommand = new RelayCommand<TweakViewModel>(ToggleGroup);
        OpenLinkCommand = new RelayCommand<string>(OpenLink);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, () => !IsBusy, ReportUnexpected);
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
        StatusText = "Проверяю текущее состояние…";

        // Независимые проверки идут параллельно: стартовый экран готов быстрее, чем при последовательной загрузке.
        var info = RefreshInfoAsync();
        var maintenance = Maintenance.InitializeAsync();
        var debloat = Debloat.InitializeAsync();
        try
        {
            await RefreshTweakStatesAsync();
            StatusText = "Готово";
        }
        finally
        {
            // Дожидаемся остальных даже при ошибке, чтобы их исключения не остались неучтёнными.
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

    private void SelectPage(AppPage page, bool selected)
    {
        if (!selected || _page == page)
        {
            return;
        }

        _page = page;
        OnPropertyChanged(nameof(IsTweaksPage));
        OnPropertyChanged(nameof(IsSystemPage));
        OnPropertyChanged(nameof(IsMaintenancePage));
        OnPropertyChanged(nameof(IsDebloatPage));
        OnPropertyChanged(nameof(IsAppsPage));
        OnPropertyChanged(nameof(IsStorePage));
        OnPropertyChanged(nameof(IsGamesPage));
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
        {
            tweak.IsSelected = tweak.IsApplied;
        }
    }

    private async Task ApplyAsync()
    {
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
                }
                else
                {
                    failed++;
                    vm.ErrorText = $"Не удалось {(apply ? "применить" : "откатить")}: {outcome.Error}";
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
        if (system.Count == 0)
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
        var snapshot = Tweaks.ToList();
        var states = await Task.Run(() => snapshot.Select(t => (Vm: t, State: ReadState(t.Tweak))).ToList());
        foreach (var (vm, state) in states)
        {
            vm.SetState(state.Applied, state.Error);
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
                var ok = _dialogs.Confirm(
                    "Обновление WinOptimizer",
                    $"{result.Message}\n\nСкачать и установить сейчас?\n\nРепозиторий: github.com/{_updates.GitHubRepo}");
                if (ok)
                    await ApplyUpdateAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Startup update check", ex);
            UpdateStatus = "Проверка обновлений не удалась";
        }
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
                if (_dialogs.Confirm("Обновление", result.Message + "\n\nУстановить сейчас?"))
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
    Tweaks,
    System,
    Maintenance,
    Debloat,
    Apps,
    Store,
    Games,
}
