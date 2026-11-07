using System.Collections.ObjectModel;
using System.Windows;
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
        _status = app.IsRecommended ? "Рекомендуется удалить" : "Установлено";
        _icon = LoadIcon(app.IconPath);
    }

    public DebloatApp App { get; }
    public string Name => App.DisplayName;
    public string Description => App.Description;
    public bool IsRecommended => App.IsRecommended;

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
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 64;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
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

    public DebloatViewModel(IDebloatService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy, ReportError);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => !IsBusy && Items.Any(i => i.IsSelected), ReportError);
        SelectRecommendedCommand = new RelayCommand(SelectRecommended, () => !IsBusy);
    }

    public ObservableCollection<DebloatItemViewModel> Items { get; } = new();

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
            var items = await Application.Current.Dispatcher.InvokeAsync(() =>
                list.Select(a => new DebloatItemViewModel(a)).ToList());

            Items.Clear();
            foreach (var item in items)
                Items.Add(item);

            StatusText = Items.Count == 0
                ? "Установленных приложений из списка нет"
                : $"Установлено из списка: {Items.Count}";
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

    private async Task RemoveSelectedAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        var choice = _dialogs.Ask(
            "Удалить выбранные приложения?",
            $"Будет удалено: {selected.Count}.\n\n" +
            string.Join("\n", selected.Take(12).Select(s => "• " + s.Name)),
            primary: "Удалить",
            secondary: "Отмена");
        if (choice != DialogChoice.Primary) return;

        IsBusy = true;
        try
        {
            var ok = 0;
            foreach (var item in selected)
            {
                StatusText = $"Удаляю {item.Name}…";
                var (success, message) = await _service.RemoveAsync(item.App);
                item.Status = message;
                if (success)
                {
                    ok++;
                    item.IsInstalled = false;
                    item.IsSelected = false;
                }
            }

            StatusText = $"Готово. Успешно: {ok} из {selected.Count}.";
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
