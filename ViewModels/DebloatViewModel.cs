using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

public sealed class DebloatItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isInstalled;
    private string _status = "";
    private ImageSource? _icon;

    public DebloatItemViewModel(DebloatApp app)
    {
        App = app;
        _isInstalled = app.IsInstalled;
        _status = app.IsRecommended
            ? "Рекомендуется удалить"
            : app.Kind == AppKind.Store ? "Приложение Windows / Store" : "Программа";
        _icon = LoadIcon(app.IconPath);
    }

    public DebloatApp App { get; }
    public string Name => App.DisplayName;
    public string Description => App.Description;
    public bool IsRecommended => App.IsRecommended;

    public AppKind Kind => App.Kind;

    public ImageSource? Icon
    {
        get => _icon;
        private set => SetField(ref _icon, value);
    }

    public bool HasIcon => Icon != null;

    public bool IsInstalled
    {
        get => _isInstalled;
        set => SetField(ref _isInstalled, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private static ImageSource? LoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.DecodePixelWidth = 64;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }

            // Иконка классической программы: из exe/dll/ico
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class DebloatViewModel : ObservableObject
{
    private readonly IDebloatService _service;
    private readonly IDialogService _dialogs;
    private bool _isBusy;
    private string _statusText = "Готово";
    private string _searchText = "";
    private int _filterMode; // 0 — все, 1 — программы, 2 — приложения Windows

    public DebloatViewModel(IDebloatService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is DebloatItemViewModel i && MatchesSearch(i);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy, ReportError);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => !IsBusy && Items.Any(i => i.IsSelected), ReportError);
        SelectRecommendedCommand = new RelayCommand(SelectRecommended, () => !IsBusy);
        RemoveOneCommand = new AsyncRelayCommand<DebloatItemViewModel>(
            item => item is null ? Task.CompletedTask : RemoveAppsAsync(new List<DebloatItemViewModel> { item }),
            item => !IsBusy && item is not null, ReportError);
        SetFilterCommand = new RelayCommand<string>(f =>
        {
            FilterMode = int.TryParse(f, out var m) ? m : 0;
        });
    }

    public ICommand RemoveOneCommand { get; }
    public ICommand SetFilterCommand { get; }

    /// <summary>0 — все, 1 — классические программы, 2 — приложения Windows (Store).</summary>
    public int FilterMode
    {
        get => _filterMode;
        set
        {
            if (!SetField(ref _filterMode, value)) return;
            OnPropertyChanged(nameof(IsFilterAll));
            OnPropertyChanged(nameof(IsFilterWin32));
            OnPropertyChanged(nameof(IsFilterStore));
            View.Refresh();
        }
    }

    public bool IsFilterAll => _filterMode == 0;
    public bool IsFilterWin32 => _filterMode == 1;
    public bool IsFilterStore => _filterMode == 2;

    public string AllLabel => $"Все ({Items.Count})";
    public string Win32Label => $"Программы ({Items.Count(i => i.Kind == AppKind.Win32)})";
    public string StoreLabel => $"Приложения Windows ({Items.Count(i => i.Kind == AppKind.Store)})";

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(AllLabel));
        OnPropertyChanged(nameof(Win32Label));
        OnPropertyChanged(nameof(StoreLabel));
    }

    public ObservableCollection<DebloatItemViewModel> Items { get; } = new();

    /// <summary>Список с поиском по названию, издателю и описанию.</summary>
    public ICollectionView View { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
                View.Refresh();
        }
    }

    private bool MatchesSearch(DebloatItemViewModel i)
    {
        if (_filterMode == 1 && i.Kind != AppKind.Win32) return false;
        if (_filterMode == 2 && i.Kind != AppKind.Store) return false;
        return string.IsNullOrWhiteSpace(_searchText)
               || i.Name.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase)
               || i.Description.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase);
    }

    public ICommand RefreshCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand SelectRecommendedCommand { get; }

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

    public async Task InitializeAsync() => await RefreshAsync();

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Сканирую установленные приложения…";
        try
        {
            var list = await _service.ListAsync();
            // Иконки BitmapImage создаём в UI-потоке
            // Иконки (в том числе из exe) извлекаем в фоне — на сотни программ это заметное время
            var items = await Task.Run(() => list.Select(a => new DebloatItemViewModel(a)).ToList());

            Items.Clear();
            foreach (var item in items)
                Items.Add(item);

            RaiseCounts();
            StatusText = Items.Count == 0
                ? "Установленных приложений не найдено"
                : $"Найдено приложений и программ: {Items.Count}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SelectRecommended()
    {
        foreach (var item in Items)
            item.IsSelected = item.IsRecommended;
    }

    private Task RemoveSelectedAsync() => RemoveAppsAsync(Items.Where(i => i.IsSelected).ToList());

    private async Task RemoveAppsAsync(List<DebloatItemViewModel> selected)
    {
        if (selected.Count == 0) return;

        var choice = _dialogs.Ask(
            selected.Count == 1 ? $"Удалить «{selected[0].Name}»?" : "Удалить выбранные приложения?",
            $"Будет удалено: {selected.Count}.\n\n" +
            string.Join("\n", selected.Take(12).Select(s => "• " + s.Name)) +
            (selected.Count > 12 ? $"\n… и ещё {selected.Count - 12}" : "") +
            "\n\nСначала запустится штатный деинсталлятор, затем программа найдёт остатки " +
            "(папки, ярлыки, записи реестра) и предложит удалить их.",
            primary: "Удалить",
            secondary: "Отмена",
            kind: DialogKind.Danger);
        if (choice != DialogChoice.Primary) return;

        IsBusy = true;
        try
        {
            var ok = 0;
            var leftovers = new List<Leftover>();
            foreach (var item in selected)
            {
                StatusText = $"Удаляю {item.Name}…";
                var (success, message) = await _service.RemoveAsync(item.App);

                // Деинсталлятор не справился — предлагаем снести программу принудительно
                if (!success && item.Kind == AppKind.Win32)
                {
                    var force = _dialogs.Ask(
                        $"Не удалось удалить «{item.Name}»",
                        message + "\n\nМожно удалить принудительно: закрыть процессы программы, стереть её папку " +
                        "и запись в списке программ.",
                        primary: "Удалить принудительно",
                        cancel: "Пропустить",
                        kind: DialogKind.Warning);
                    if (force == DialogChoice.Primary)
                    {
                        StatusText = $"Принудительно удаляю {item.Name}…";
                        (success, message) = await _service.ForceRemoveAsync(item.App);
                    }
                }

                item.Status = message;
                if (!success) continue;

                ok++;
                item.IsInstalled = false;
                item.IsSelected = false;

                StatusText = $"Ищу остатки {item.Name}…";
                leftovers.AddRange(await _service.FindLeftoversAsync(item.App));
            }

            if (leftovers.Count > 0)
            {
                var distinct = leftovers.DistinctBy(l => l.Kind + "|" + l.Path).ToList();
                var answer = _dialogs.Ask(
                    "Найдены остатки",
                    $"После удаления остались следы ({distinct.Count}):\n\n" +
                    string.Join("\n", distinct.Take(25).Select(l => "• " + l)) +
                    (distinct.Count > 25 ? $"\n… и ещё {distinct.Count - 25}" : "") +
                    "\n\nУдалить их?",
                    primary: "Удалить остатки",
                    cancel: "Оставить",
                    kind: DialogKind.Warning,
                    subtitle: "Очистка без остатка");
                if (answer == DialogChoice.Primary)
                {
                    StatusText = "Удаляю остатки…";
                    var (removed, failed) = await _service.RemoveLeftoversAsync(distinct);
                    if (failed.Count > 0)
                        _dialogs.Info("Часть остатков не удалена",
                            string.Join("\n", failed.Take(10)) +
                            "\n\nВозможно, файлы заняты процессом — перезагрузите ПК и повторите.",
                            DialogKind.Warning);
                    StatusText = $"Готово. Удалено: {ok} из {selected.Count}, остатков стёрто: {removed}.";
                    await RefreshAsync();
                    return;
                }
            }

            StatusText = $"Готово. Удалено: {ok} из {selected.Count}.";
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReportError(Exception ex)
    {
        StatusText = "Ошибка";
        _dialogs.Info("Ошибка деблоата", ex.Message);
    }
}
