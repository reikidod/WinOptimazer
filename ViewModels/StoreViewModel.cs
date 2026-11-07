using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

public sealed class StoreItemViewModel : ObservableObject
{
    private bool _isBusy;
    private string _publisher;
    private string _description;
    private ImageSource? _icon;

    public StoreItemViewModel(StoreAppInfo app)
    {
        App = app;
        _publisher = string.IsNullOrEmpty(app.Publisher) ? app.Source : app.Publisher;
        _description = app.Description ?? $"Источник: {app.Source}";
    }

    public StoreAppInfo App { get; }
    public string Name => App.Name;
    public string Id => App.Id;

    /// <summary>Первая буква названия — заглушка, пока иконка не загрузилась (или её нет).</summary>
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

    public string Publisher
    {
        get => _publisher;
        private set => SetField(ref _publisher, value);
    }

    public string Description
    {
        get => _description;
        private set => SetField(ref _description, value);
    }

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (SetField(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon != null;

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    /// <summary>Подгружает иконку, издателя и описание из каталога Store; вызывать из UI-потока.</summary>
    public async Task LoadMetadataAsync(IMsStoreService store, CancellationToken ct)
    {
        var meta = await store.GetMetadataAsync(App, ct);
        if (meta is null || ct.IsCancellationRequested) return;

        if (!string.IsNullOrWhiteSpace(meta.Publisher)) Publisher = meta.Publisher!;
        if (App.Description is null && !string.IsNullOrWhiteSpace(meta.Description))
            Description = meta.Description!;

        if (meta.Icon is { Length: > 0 })
        {
            try
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(meta.Icon);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.DecodePixelWidth = 96;
                bmp.EndInit();
                bmp.Freeze();
                Icon = bmp;
            }
            catch
            {
                // битый файл иконки — остаётся буква
            }
        }
    }
}

public sealed class StoreViewModel : ObservableObject
{
    private readonly IMsStoreService _store;
    private readonly IDialogService _dialogs;
    private string _query = "";
    private string _statusText = "Каталог Microsoft Store";
    private bool _isBusy;
    private bool _wingetOk;
    private string _wingetStatus = "Проверяю winget…";
    private CancellationTokenSource? _iconsCts;

    public StoreViewModel(IMsStoreService store, IDialogService dialogs)
    {
        _store = store;
        _dialogs = dialogs;

        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy, ReportError);
        InstallCommand = new AsyncRelayCommand<StoreItemViewModel>(InstallAsync, _ => !IsBusy, ReportError);
        OpenInStoreCommand = new RelayCommand<StoreItemViewModel>(OpenInStore);
        OpenHomeCommand = new RelayCommand(() => _store.OpenStoreHome());
        OpenUpdatesCommand = new RelayCommand(() => _store.OpenStoreUpdates());
        OpenLibraryCommand = new RelayCommand(() => _store.OpenStoreLibrary());
        RefreshFeaturedCommand = new RelayCommand(LoadFeatured);

        CheckWingetCommand = new AsyncRelayCommand(CheckWingetAsync, () => !IsBusy, ReportError);
        RepairWingetCommand = new AsyncRelayCommand(RepairWingetAsync, () => !IsBusy, ReportError);

        LoadFeatured();
        _ = CheckWingetAsync();
    }

    public ICommand CheckWingetCommand { get; }
    public ICommand RepairWingetCommand { get; }

    public string WingetStatus
    {
        get => _wingetStatus;
        private set => SetField(ref _wingetStatus, value);
    }

    /// <summary>Проверка winget: путь, версия, источник msstore.</summary>
    private async Task CheckWingetAsync()
    {
        WingetStatus = "Проверяю winget…";
        var st = await _store.CheckWingetAsync();
        WingetAvailable = st.Available && st.MsStoreSource;
        WingetStatus = (st.Available && st.MsStoreSource ? "✔ " : "⚠ ") + st.Message;
        StatusText = WingetAvailable
            ? "winget готов — поиск и установка прямо в WinOptimizer."
            : "winget не готов: установите или почините его кнопками выше.";
    }

    private async Task RepairWingetAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(m => { StatusText = m; WingetStatus = m; });
            var (ok, msg) = await _store.EnsureWingetAsync(progress);
            await CheckWingetAsync();
            _dialogs.Info("winget", msg, ok ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Иконки и описания подгружаем в фоне по 4 штуки: карточки появляются сразу, картинки — по мере загрузки.</summary>
    private async Task LoadIconsAsync()
    {
        _iconsCts?.Cancel();
        var cts = _iconsCts = new CancellationTokenSource();
        var items = Results.ToList();
        using var gate = new SemaphoreSlim(4);
        try
        {
            await Task.WhenAll(items.Select(async item =>
            {
                await gate.WaitAsync(cts.Token);
                try { await item.LoadMetadataAsync(_store, cts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Logger.Error("Иконка Store: " + item.Name, ex); }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException)
        {
            // список обновился — старая загрузка не нужна
        }
    }

    public ObservableCollection<StoreItemViewModel> Results { get; } = new();

    public ICommand SearchCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand OpenInStoreCommand { get; }
    public ICommand OpenHomeCommand { get; }
    public ICommand OpenUpdatesCommand { get; }
    public ICommand OpenLibraryCommand { get; }
    public ICommand RefreshFeaturedCommand { get; }

    public string Query
    {
        get => _query;
        set => SetField(ref _query, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool WingetAvailable
    {
        get => _wingetOk;
        private set => SetField(ref _wingetOk, value);
    }

    private void LoadFeatured()
    {
        Results.Clear();
        foreach (var app in _store.Featured)
            Results.Add(new StoreItemViewModel(app));
        StatusText = $"Избранные приложения Store ({Results.Count}). Введите запрос для поиска.";
        _ = LoadIconsAsync();
    }

    private async Task SearchAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = string.IsNullOrWhiteSpace(Query)
                ? "Загрузка избранного…"
                : $"Поиск «{Query}»…";
            var list = await _store.SearchAsync(Query);
            Results.Clear();
            foreach (var app in list)
                Results.Add(new StoreItemViewModel(app));
            StatusText = Results.Count == 0
                ? "Ничего не найдено."
                : $"Найдено: {Results.Count}";
            _ = LoadIconsAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync(StoreItemViewModel? item)
    {
        if (item is null) return;

        // Перед установкой проверяем winget; если его нет — предлагаем поставить
        if (!_store.IsWingetAvailable())
        {
            var choice = _dialogs.Ask(
                "winget не найден",
                "Для установки приложений из Store нужен winget (App Installer). Установить его сейчас?",
                primary: "Установить winget", cancel: "Отмена", kind: DialogKind.Warning);
            if (choice != DialogChoice.Primary) return;
            await RepairWingetAsync();
            if (!_store.IsWingetAvailable()) return;
        }

        IsBusy = true;
        item.IsBusy = true;
        try
        {
            var progress = new Progress<string>(m => StatusText = m);
            var (ok, msg) = await _store.InstallAsync(item.App, progress);
            StatusText = msg;
            _dialogs.Info("Microsoft Store", msg, ok ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            item.IsBusy = false;
            IsBusy = false;
        }
    }

    private void OpenInStore(StoreItemViewModel? item)
    {
        if (item is null) return;
        _store.OpenInOfficialStore(item.App);
        StatusText = $"Открыт Store: {item.Name}";
    }

    private void ReportError(Exception ex)
    {
        StatusText = "Ошибка";
        _dialogs.Info("Microsoft Store", ex.Message, DialogKind.Warning);
    }
}
