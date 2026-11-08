using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

/// <summary>Раздел списка удаления.</summary>
public enum AppCategory
{
    Game,
    Program,
    Windows,
}

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
        _status = app.Launcher != null
            ? $"Игра из {app.Launcher}: удаляется только через лаунчер"
            : app.IsRecommended
            ? "Рекомендуется удалить"
            : app.Kind == AppKind.Store ? (app.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? "Приложение Microsoft" : "Приложение Microsoft Store") : "Программа";
        _icon = LoadIcon(app.IconPath);
        Category = Categorize(app);
    }

    public AppCategory Category { get; }

    private static readonly System.Text.RegularExpressions.Regex StoreGameName = new(
        @"solitaire|minecraft|mahjong|candy|bubble|asphalt|gardenscapes|homescapes|royal|empires|" +
        @"jigsaw|sudoku|wordament|disney", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly string[] GameFolders =
    {
        @"\steamapps\", @"\Epic Games\", @"\GOG Games\", @"\GOG Galaxy\Games\", @"\Riot Games\",
        @"\Ubisoft Game Launcher\games\", @"\EA Games\", @"\Origin Games\", @"\XboxGames\", @"\Games\",
    };

    private static readonly string[] WindowsPublisherMarks =
    {
        "Microsoft", "Windows", "Xbox", "Intel", "NVIDIA", "AMD", "Realtek", "Advanced Micro Devices",
    };

    /// <summary>
    /// Игры — всё от лаунчеров и из игровых папок; «Приложения Windows» — пакеты Store, компоненты и программы
    /// Microsoft и подобные системные (драйверы, среды выполнения); остальное — обычные программы.
    /// </summary>
    private static AppCategory Categorize(DebloatApp app)
    {
        if (app.Launcher != null) return AppCategory.Game;

        var loc = app.InstallLocation ?? "";
        if (app.Kind == AppKind.Win32 && !loc.Contains(@"\Launcher", StringComparison.OrdinalIgnoreCase)
            && GameFolders.Any(f => loc.Contains(f, StringComparison.OrdinalIgnoreCase)))
            return AppCategory.Game;
        if (app.Kind == AppKind.Store && StoreGameName.IsMatch(app.PackageName))
            return AppCategory.Game;

        if (app.Kind == AppKind.Store) return AppCategory.Windows;

        var name = app.DisplayName;
        if (WindowsPublisherMarks.Any(m => app.Publisher.Contains(m, StringComparison.OrdinalIgnoreCase))
            || name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".NET", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase))
            return AppCategory.Windows;

        return AppCategory.Program;
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
    private int _filterMode; // 0 — все, 1 — игры, 2 — программы, 3 — приложения Windows и подобные

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

    /// <summary>0 — все, 1 — игры, 2 — программы, 3 — приложения Windows и подобные.</summary>
    public int FilterMode
    {
        get => _filterMode;
        set
        {
            if (!SetField(ref _filterMode, value)) return;
            OnPropertyChanged(nameof(IsFilterAll));
            OnPropertyChanged(nameof(IsFilterGames));
            OnPropertyChanged(nameof(IsFilterPrograms));
            OnPropertyChanged(nameof(IsFilterWindows));
            View.Refresh();
        }
    }

    public bool IsFilterAll => _filterMode == 0;
    public bool IsFilterGames => _filterMode == 1;
    public bool IsFilterPrograms => _filterMode == 2;
    public bool IsFilterWindows => _filterMode == 3;

    public string AllLabel => $"Все ({Items.Count})";
    public string GamesLabel => $"Игры ({Items.Count(i => i.Category == AppCategory.Game)})";
    public string ProgramsLabel => $"Программы ({Items.Count(i => i.Category == AppCategory.Program)})";
    public string WindowsLabel => $"Windows и системные ({Items.Count(i => i.Category == AppCategory.Windows)})";

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(AllLabel));
        OnPropertyChanged(nameof(GamesLabel));
        OnPropertyChanged(nameof(ProgramsLabel));
        OnPropertyChanged(nameof(WindowsLabel));
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
        if (_filterMode == 1 && i.Category != AppCategory.Game) return false;
        if (_filterMode == 2 && i.Category != AppCategory.Program) return false;
        if (_filterMode == 3 && i.Category != AppCategory.Windows) return false;
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
            (selected.Any(i => i.App.Launcher != null)
                ? "\n\nИгры из Steam и других лаунчеров будут удалены через сам лаунчер — так он останется целым."
                : "") +
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
            var launcherGames = 0;
            var leftovers = new List<Leftover>();
            foreach (var item in selected)
            {
                StatusText = $"Удаляю {item.Name}…";
                var (success, message) = await _service.RemoveAsync(item.App);

                // Игра из лаунчера: удаление делает сам лаунчер, наш список и остатки не трогаем
                if (item.App.Launcher != null)
                {
                    item.Status = message;
                    item.IsSelected = false;
                    launcherGames++;
                    continue;
                }

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

            StatusText = launcherGames > 0
                ? $"Готово. Удалено: {ok}. Игры из лаунчеров ({launcherGames}) удалите в окне лаунчера, затем нажмите «Обновить»."
                : $"Готово. Удалено: {ok} из {selected.Count}.";
            if (launcherGames > 0) return;
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
