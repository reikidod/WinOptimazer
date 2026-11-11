using System.Collections.ObjectModel;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

public sealed class AppItemViewModel : ObservableObject
{
    private bool _isInstalled;
    private string _status = "";

    public AppItemViewModel(AppCatalogItem app, bool installed)
    {
        App = app;
        _isInstalled = installed;
        _status = installed ? "Установлено" : "Не установлено";
    }

    public AppCatalogItem App { get; }
    public string Name => App.Name;
    public string Description => App.Description;
    public string ActionText => IsInstalled ? "Удалить" : "Скачать и установить";

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetField(ref _isInstalled, value))
            {
                Status = value ? "Установлено" : "Не установлено";
                OnPropertyChanged(nameof(ActionText));
            }
        }
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }
}

public sealed class ZapretBatItemViewModel : ObservableObject
{
    private string _note = "";
    private bool _isActive;

    public ZapretBatItemViewModel(ZapretBatInfo info)
    {
        Info = info;
    }

    public ZapretBatInfo Info { get; }
    public string FileName => Info.FileName;
    public string RelativePath => Info.RelativePath;
    public string Kind => Info.Kind;
    public string FullPath => Info.FullPath;

    public bool IsStrategy =>
        Kind.Contains("стратег", StringComparison.OrdinalIgnoreCase)
        || Kind.Contains("базов", StringComparison.OrdinalIgnoreCase)
        || Kind.Contains("альтернат", StringComparison.OrdinalIgnoreCase)
        || Kind.Contains("сервис", StringComparison.OrdinalIgnoreCase);

    public string Note
    {
        get => _note;
        set => SetField(ref _note, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }
}

public sealed class AppsViewModel : ObservableObject
{
    private readonly IAppInstallService _apps;
    private readonly IZapretService _zapret;
    private readonly ITgWsProxyService _tgWs;
    private readonly IDialogService _dialogs;
    private bool _isBusy;
    private string _statusText = "Готово";
    private string _zapretStatus = "";
    private bool _zapretInstalled;
    private bool _zapretRunning;
    private string _tgStatus = "";
    private bool _tgInstalled;
    private bool _tgRunning;

    public AppsViewModel(IAppInstallService apps, IZapretService zapret, ITgWsProxyService tgWs, IDialogService dialogs)
    {
        _apps = apps;
        _zapret = zapret;
        _tgWs = tgWs;
        _dialogs = dialogs;

        foreach (var app in _apps.Catalog)
            Apps.Add(new AppItemViewModel(app, _apps.IsInstalled(app)));

        InstallAppCommand = new AsyncRelayCommand<AppItemViewModel>(ManageAppAsync, _ => !IsBusy, ReportError);
        RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
        ZapretInstallCommand = new AsyncRelayCommand(ZapretInstallAsync, () => !IsBusy && !ZapretInstalled, ReportError);
        ZapretLocateCommand = new AsyncRelayCommand(ZapretLocateAsync, () => !IsBusy, ReportError);
        ZapretBrowseCommand = new RelayCommand(ZapretBrowse, () => !IsBusy);
        ZapretStartCommand = new AsyncRelayCommand(ZapretStartAsync, () => !IsBusy && ZapretInstalled, ReportError);
        ZapretStopCommand = new AsyncRelayCommand(ZapretStopAsync, () => !IsBusy && ZapretInstalled, ReportError);
        ZapretUninstallCommand = new AsyncRelayCommand(ZapretUninstallAsync, () => !IsBusy && ZapretInstalled, ReportError);
        TestBatCommand = new AsyncRelayCommand<ZapretBatItemViewModel>(TestBatAsync, _ => !IsBusy && ZapretInstalled, ReportError);
        AutoTestAllCommand = new AsyncRelayCommand(AutoTestAllAsync, () => !IsBusy && ZapretInstalled, ReportError);
        OfficialTestsCommand = new AsyncRelayCommand(OfficialTestsAsync, () => !IsBusy && ZapretInstalled, ReportError);
        TgInstallCommand = new AsyncRelayCommand(TgInstallAsync, () => !IsBusy && !TgInstalled, ReportError);
        TgLocateCommand = new AsyncRelayCommand(TgLocateAsync, () => !IsBusy, ReportError);
        TgBrowseCommand = new RelayCommand(TgBrowse, () => !IsBusy);
        TgStartCommand = new AsyncRelayCommand(TgStartAsync, () => !IsBusy && TgInstalled, ReportError);
        TgStopCommand = new AsyncRelayCommand(TgStopAsync, () => !IsBusy && TgInstalled, ReportError);
        TgUninstallCommand = new AsyncRelayCommand(TgUninstallAsync, () => !IsBusy && TgInstalled, ReportError);
        Recommended = new RecommendedFilesViewModel(new RecommendedFilesService(), dialogs);
        RefreshZapret();
        RefreshTg();
        _ = AutoDetectAsync();
    }

    /// <summary>Рекомендуемые файлы: Visual C++ и DirectX.</summary>
    public RecommendedFilesViewModel Recommended { get; }

    public ObservableCollection<AppItemViewModel> Apps { get; } = new();
    public ObservableCollection<ZapretBatItemViewModel> ZapretBats { get; } = new();

    public ICommand InstallAppCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ZapretInstallCommand { get; }
    public ICommand ZapretLocateCommand { get; }
    public ICommand ZapretBrowseCommand { get; }
    public ICommand TgLocateCommand { get; }
    public ICommand TgBrowseCommand { get; }
    public ICommand ZapretStartCommand { get; }
    public ICommand ZapretStopCommand { get; }
    public ICommand ZapretUninstallCommand { get; }
    public ICommand TestBatCommand { get; }
    public ICommand AutoTestAllCommand { get; }
    public ICommand OfficialTestsCommand { get; }
    public ICommand TgInstallCommand { get; }
    public ICommand TgStartCommand { get; }
    public ICommand TgStopCommand { get; }
    public ICommand TgUninstallCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string ZapretStatusText
    {
        get => _zapretStatus;
        private set => SetField(ref _zapretStatus, value);
    }

    public bool ZapretInstalled
    {
        get => _zapretInstalled;
        private set
        {
            if (SetField(ref _zapretInstalled, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool ZapretRunning
    {
        get => _zapretRunning;
        private set => SetField(ref _zapretRunning, value);
    }

    public string TgStatusText
    {
        get => _tgStatus;
        private set => SetField(ref _tgStatus, value);
    }

    public bool TgInstalled
    {
        get => _tgInstalled;
        private set
        {
            if (SetField(ref _tgInstalled, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool TgRunning
    {
        get => _tgRunning;
        private set => SetField(ref _tgRunning, value);
    }

    public void Refresh()
    {
        foreach (var item in Apps)
            item.IsInstalled = _apps.IsInstalled(item.App);
        RefreshZapret();
        RefreshTg();
        StatusText = "Обновлено";
    }

    private void RefreshZapret()
    {
        var s = _zapret.GetStatus();
        ZapretInstalled = s.Installed;
        ZapretRunning = s.ServiceRunning;
        ZapretStatusText = s.Message + (s.Installed ? $"\nПуть: {s.InstallPath}" : "");

        ZapretBats.Clear();
        if (!s.Installed) return;

        foreach (var bat in _zapret.ListBatFiles())
        {
            var vm = new ZapretBatItemViewModel(bat);
            if (bat.Kind.Contains("базов", StringComparison.OrdinalIgnoreCase))
                vm.Note = "Обычно начинают с этого";
            ZapretBats.Add(vm);
        }
    }

    private async Task ManageAppAsync(AppItemViewModel? item)
    {
        if (item is null) return;
        item.IsInstalled = _apps.IsInstalled(item.App);
        if (item.IsInstalled)
        {
            var choice = _dialogs.Ask(
                $"Удалить {item.Name}?",
                "Будет запущен деинсталлятор, зарегистрированный в Windows. WinOptimizer не удаляет папки и пользовательские данные вручную.",
                primary: "Удалить", secondary: "Отмена", kind: DialogKind.Danger);
            if (choice != DialogChoice.Primary) return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(m => StatusText = m);
            var result = item.IsInstalled
                ? await _apps.UninstallAsync(item.App, progress)
                : await _apps.DownloadAndInstallAsync(item.App, progress);
            StatusText = result.Message;
            item.IsInstalled = _apps.IsInstalled(item.App);
            _dialogs.Info("Операция с приложением", result.Message,
                result.Ok ? DialogKind.Success : DialogKind.Warning);
        }
        finally { IsBusy = false; }
    }

    private async Task ZapretInstallAsync()
    {
        var choice = _dialogs.Ask(
            "Установить Zapret (Flowseal)?",
            "Будет скачан последний release с GitHub (Flowseal/zapret-discord-youtube), распакован в каталог данных WinOptimizer и при возможности установлен как служба.\n\nТребуются права администратора.",
            primary: "Скачать и установить",
            secondary: "Отмена");
        if (choice != DialogChoice.Primary) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(m => StatusText = m);
            var (ok, msg) = await _zapret.DownloadAndInstallAsync(progress);
            StatusText = msg;
            RefreshZapret();
            _dialogs.Info("Zapret", msg, ok ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ZapretStartAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "Запуск Zapret…";
            var (ok, msg) = await _zapret.StartServiceAsync();
            StatusText = msg;
            RefreshZapret();
        }
        finally { IsBusy = false; }
    }

    private async Task ZapretStopAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "Остановка Zapret…";
            var (ok, msg) = await _zapret.StopServiceAsync();
            StatusText = msg;
            RefreshZapret();
        }
        finally { IsBusy = false; }
    }

    private async Task ZapretUninstallAsync()
    {
        var choice = _dialogs.Ask("Удалить Zapret?", "Служба будет остановлена и удалена. Файлы удаляются, только если Zapret был установлен этим приложением; найденный в другой папке остаётся на месте.", primary: "Удалить", secondary: "Отмена", kind: DialogKind.Danger);
        if (choice != DialogChoice.Primary) return;
        IsBusy = true;
        try
        {
            var (ok, msg) = await _zapret.UninstallAsync();
            StatusText = msg;
            RefreshZapret();
        }
        finally { IsBusy = false; }
    }

    private async Task TestBatAsync(ZapretBatItemViewModel? item)
    {
        if (item is null) return;

        var choice = _dialogs.Ask(
            "Протестировать пресет?",
            $"Будет остановлен текущий процесс Zapret и запущен:\n\n«{item.FileName}»\n({item.Kind})\n\n" +
            "После запуска проверьте Discord и YouTube 1–2 минуты.\n" +
            "Если не работает — нажмите «Остановить» и попробуйте другой bat.\n\n" +
            "Часто самым стабильным оказывается general / default.",
            primary: "Запустить тест",
            secondary: "Отмена",
            kind: DialogKind.Warning,
            subtitle: "Переключение стратегии Zapret");
        if (choice != DialogChoice.Primary) return;

        IsBusy = true;
        try
        {
            StatusText = $"Тест: {item.FileName}…";
            foreach (var b in ZapretBats)
            {
                b.IsActive = false;
                if (b.Note.StartsWith("Активен", StringComparison.Ordinal) || b.Note.StartsWith("Лучший", StringComparison.Ordinal))
                    b.Note = "";
            }

            var (ok, msg) = await _zapret.TestBatAsync(item.FullPath);
            StatusText = msg;
            if (ok)
            {
                item.IsActive = true;
                item.Note = "Активен — проверьте Discord / YouTube";
            }
            else
            {
                item.Note = msg;
            }
            RefreshZapretStatusOnly();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AutoTestAllAsync()
    {
        var choice = _dialogs.Ask(
            "Подобрать лучшую стратегию?",
            "Раньше все bat запускались по очереди — из‑за этого часто оставался последний.\n\n" +
            "Сейчас так:\n" +
            "1. Все процессы Zapret будут остановлены\n" +
            "2. Откроется service.bat\n" +
            "3. Выберите: Run Tests → Standard → All\n" +
            "4. В конце Zapret сам напишет лучшую стратегию\n\n" +
            "Ни один bat не стартует сам.",
            primary: "Открыть тесты",
            secondary: "Отмена",
            kind: DialogKind.Default,
            subtitle: "Официальный подбор Zapret");
        if (choice != DialogChoice.Primary) return;

        IsBusy = true;
        try
        {
            foreach (var b in ZapretBats)
            {
                b.IsActive = false;
                b.Note = "";
            }

            var progress = new Progress<string>(m => StatusText = m);
            var (ok, msg, _) = await _zapret.AutoTestStrategiesAsync(progress);
            StatusText = ok ? "Открыты официальные тесты Zapret" : msg;
            _dialogs.Info("Тесты Zapret", msg, ok ? DialogKind.Success : DialogKind.Warning);
            RefreshZapretStatusOnly();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OfficialTestsAsync()
    {
        var choice = _dialogs.Ask(
            "Официальные тесты Zapret",
            "Откроется service.bat от администратора.\n\n" +
            "В меню выберите:\n" +
            "  Run Tests → Standard → All\n\n" +
            "В конце утилита Zapret сама напишет самую подходящую стратегию.\n" +
            "После этого: Install Service → выберите её для автозапуска.",
            primary: "Открыть service.bat",
            secondary: "Отмена",
            kind: DialogKind.Default,
            subtitle: "Run Tests → лучшая стратегия");
        if (choice != DialogChoice.Primary) return;

        IsBusy = true;
        try
        {
            var (ok, msg) = await _zapret.RunOfficialTestsAsync();
            StatusText = msg;
            _dialogs.Info("service.bat", msg, ok ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// При запуске: если Zapret или TG WS Proxy не нашлись быстрой проверкой (процессы, службы, автозагрузка,
    /// запомненный путь), ищем их по всем дискам в фоне — устанавливать заново не нужно, если они уже есть.
    /// </summary>
    private async Task AutoDetectAsync()
    {
        try
        {
            var probes = new Dictionary<string, Func<string, string?>>();
            if (!ZapretInstalled) probes[_zapret.LocatorKey] = _zapret.LocatorProbe;
            if (!TgInstalled) probes[_tgWs.LocatorKey] = _tgWs.LocatorProbe;
            if (probes.Count == 0) return;

            StatusText = "Ищу Zapret и TG WS Proxy на компьютере…";
            var progress = new Progress<string>(m => StatusText = m);
            var hits = await Task.Run(() => AppLocator.Search(probes, progress)).ConfigureAwait(true);

            if (hits.TryGetValue(_zapret.LocatorKey, out var z)) _zapret.Adopt(z);
            if (hits.TryGetValue(_tgWs.LocatorKey, out var t)) _tgWs.Adopt(t);
            RefreshZapret();
            RefreshTg();

            var names = new List<string>();
            if (hits.ContainsKey(_zapret.LocatorKey)) names.Add("Zapret");
            if (hits.ContainsKey(_tgWs.LocatorKey)) names.Add("TG WS Proxy");
            StatusText = names.Count > 0
                ? "Найдено на компьютере: " + string.Join(", ", names)
                : "Готово";
        }
        catch (Exception ex)
        {
            Logger.Error("Автопоиск Zapret / TG WS Proxy", ex);
            StatusText = "Готово";
        }
    }

    private async Task ZapretLocateAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(m => StatusText = m);
            var (found, msg) = await _zapret.FindExistingAsync(progress);
            StatusText = msg;
            RefreshZapret();
            _dialogs.Info("Zapret", msg, found ? DialogKind.Success : DialogKind.Warning);
        }
        finally { IsBusy = false; }
    }

    private void ZapretBrowse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Выберите папку с Zapret (где лежат service.bat и bin\\winws.exe)" };
        if (dialog.ShowDialog() != true) return;

        if (_zapret.Adopt(dialog.FolderName))
        {
            RefreshZapret();
            StatusText = "Zapret добавлен: " + dialog.FolderName;
        }
        else
        {
            _dialogs.Info("Zapret", "В этой папке не найден Zapret (нужен winws.exe и хотя бы один bat-файл).", DialogKind.Warning);
        }
    }

    private async Task TgLocateAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(m => StatusText = m);
            var (found, msg) = await _tgWs.FindExistingAsync(progress);
            StatusText = msg;
            RefreshTg();
            _dialogs.Info("TG WS Proxy", msg, found ? DialogKind.Success : DialogKind.Warning);
        }
        finally { IsBusy = false; }
    }

    private void TgBrowse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите TgWsProxy_windows.exe",
            Filter = "TG WS Proxy (*.exe)|*.exe",
        };
        if (dialog.ShowDialog() != true) return;

        if (_tgWs.Adopt(dialog.FileName))
        {
            RefreshTg();
            StatusText = "TG WS Proxy добавлен: " + dialog.FileName;
        }
        else
        {
            _dialogs.Info("TG WS Proxy", "Это не похоже на TgWsProxy_windows.exe.", DialogKind.Warning);
        }
    }

    private void RefreshZapretStatusOnly()
    {
        var s = _zapret.GetStatus();
        ZapretInstalled = s.Installed;
        ZapretRunning = s.ServiceRunning;
        ZapretStatusText = s.Message + (s.Installed ? $"\nПуть: {s.InstallPath}" : "");
    }

    private void RefreshTg()
    {
        var s = _tgWs.GetStatus();
        TgInstalled = s.Installed;
        TgRunning = s.Running;
        TgStatusText = s.Message + (s.Installed ? $"\nПуть: {s.InstallPath}" : "");
    }

    private async Task TgInstallAsync()
    {
        var choice = _dialogs.Ask(
            "Установить TG WS Proxy?",
            "Скачается TgWsProxy_windows.exe с GitHub (Flowseal/tg-ws-proxy).\n" +
            "Локальный MTProto-прокси через WebSocket для Telegram Desktop.\n" +
            "После запуска: трей → Open in Telegram, либо вручную 127.0.0.1:1443.",
            primary: "Скачать",
            secondary: "Отмена");
        if (choice != DialogChoice.Primary) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(m => StatusText = m);
            var (ok, msg) = await _tgWs.DownloadAndInstallAsync(progress);
            StatusText = msg;
            RefreshTg();
            _dialogs.Info("TG WS Proxy", msg, ok ? DialogKind.Success : DialogKind.Warning);
        }
        finally { IsBusy = false; }
    }

    private async Task TgStartAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "Запуск TG WS Proxy…";
            var (ok, msg) = await _tgWs.StartAsync();
            StatusText = msg;
            RefreshTg();
        }
        finally { IsBusy = false; }
    }

    private async Task TgStopAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "Остановка TG WS Proxy…";
            var (ok, msg) = await _tgWs.StopAsync();
            StatusText = msg;
            RefreshTg();
        }
        finally { IsBusy = false; }
    }

    private async Task TgUninstallAsync()
    {
        var choice = _dialogs.Ask("Удалить TG WS Proxy?", "Процесс будет остановлен. Файлы удаляются, только если программа была установлена этим приложением.",
            primary: "Удалить", secondary: "Отмена", kind: DialogKind.Danger);
        if (choice != DialogChoice.Primary) return;
        IsBusy = true;
        try
        {
            var (ok, msg) = await _tgWs.UninstallAsync();
            StatusText = msg;
            RefreshTg();
        }
        finally { IsBusy = false; }
    }

    private void ReportError(Exception ex)
    {
        StatusText = "Ошибка";
        _dialogs.Info("Ошибка", ex.Message, DialogKind.Warning);
    }
}
