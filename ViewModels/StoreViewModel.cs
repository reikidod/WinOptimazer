using System.Collections.ObjectModel;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

public sealed class StoreItemViewModel : ObservableObject
{
    private bool _isBusy;

    public StoreItemViewModel(StoreAppInfo app)
    {
        App = app;
    }

    public StoreAppInfo App { get; }
    public string Name => App.Name;
    public string Id => App.Id;
    public string Publisher => string.IsNullOrEmpty(App.Publisher) ? App.Source : App.Publisher;
    public string Description => App.Description ?? $"Источник: {App.Source}";

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
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

        _wingetOk = _store.IsWingetAvailable();
        StatusText = _wingetOk
            ? "winget найден — поиск и установка прямо в WinOptimizer."
            : "winget не найден — установите App Installer, иначе каталог Store недоступен.";
        LoadFeatured();
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
    }

    private async Task SearchAsync()
    {
        IsBusy = true;
        try
        {
            WingetAvailable = _store.IsWingetAvailable();
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
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync(StoreItemViewModel? item)
    {
        if (item is null) return;
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
