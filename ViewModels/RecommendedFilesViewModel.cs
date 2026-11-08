using System.Collections.ObjectModel;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

public sealed class RecommendedItemViewModel : ObservableObject
{
    private string _status = "";
    private string _progressText = "";
    private double _progress;
    private bool _isBusy;

    public RecommendedItemViewModel(RecommendedFile file)
    {
        File = file;
    }

    public RecommendedFile File { get; }
    public string Name => File.Name;
    public string Description => File.Description;
    public string Note => File.Note;
    public string Category => File.Category;

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetField(ref _progressText, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }
}

/// <summary>Раскрывающаяся группа рекомендуемых файлов.</summary>
public sealed class RecommendedGroupViewModel : ObservableObject
{
    private bool _isExpanded = true;

    public RecommendedGroupViewModel(string title, IEnumerable<RecommendedItemViewModel> items)
    {
        Title = title;
        foreach (var item in items)
            Items.Add(item);
    }

    public string Title { get; }
    public ObservableCollection<RecommendedItemViewModel> Items { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
                OnPropertyChanged(nameof(Arrow));
        }
    }

    public string Arrow => IsExpanded ? "▼" : "▶";

    public int Count => Items.Count;
}

public sealed class RecommendedFilesViewModel : ObservableObject
{
    private readonly IRecommendedFilesService _service;
    private readonly IDialogService _dialogs;
    private bool _anyBusy;

    public RecommendedFilesViewModel(IRecommendedFilesService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;

        var allItems = new List<RecommendedItemViewModel>();
        foreach (var f in service.Files)
            allItems.Add(new RecommendedItemViewModel(f));

        // Порядок групп фиксированный
        var order = new[] { "Зависимости", "Программы для оптимизации", "Базовые утилиты" };
        foreach (var cat in order)
        {
            var catItems = allItems.Where(i => i.Category == cat).ToList();
            if (catItems.Count > 0)
                Groups.Add(new RecommendedGroupViewModel(cat, catItems));
        }

        // На всякий случай — категории, которых нет в order
        foreach (var g in allItems.GroupBy(i => i.Category).Where(g => !order.Contains(g.Key)))
            Groups.Add(new RecommendedGroupViewModel(g.Key, g));

        InstallCommand = new AsyncRelayCommand<RecommendedItemViewModel>(InstallAsync, _ => !_anyBusy, ex =>
            _dialogs.Info("Рекомендуемые файлы", ex.Message, DialogKind.Warning));
        ToggleGroupCommand = new RelayCommand<RecommendedGroupViewModel>(g =>
        {
            if (g != null) g.IsExpanded = !g.IsExpanded;
        });
        _ = RefreshStatusAsync();
    }

    public ObservableCollection<RecommendedGroupViewModel> Groups { get; } = new();

    /// <summary>Плоский список (для совместимости, если где-то ещё используется).</summary>
    public IEnumerable<RecommendedItemViewModel> Items => Groups.SelectMany(g => g.Items);

    public ICommand InstallCommand { get; }
    public ICommand ToggleGroupCommand { get; }

    private async Task RefreshStatusAsync()
    {
        foreach (var item in Items)
            item.Status = await Task.Run(() => _service.GetStatus(item.File));
    }

    private async Task InstallAsync(RecommendedItemViewModel? item)
    {
        if (item is null) return;

        if (item.File.ConfirmText is { } confirm &&
            _dialogs.Ask(item.Name, confirm, primary: "Установить", cancel: "Отмена", kind: DialogKind.Warning) != DialogChoice.Primary)
            return;

        _anyBusy = true;
        item.IsBusy = true;
        item.Progress = 0;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var progress = new Progress<(string Text, double Percent)>(p =>
            {
                item.ProgressText = p.Text;
                item.Progress = p.Percent;
            });
            var (ok, msg) = await _service.InstallAsync(item.File, progress);
            _dialogs.Info(item.Name, msg, ok ? DialogKind.Success : DialogKind.Warning);
            item.Status = await Task.Run(() => _service.GetStatus(item.File));
        }
        finally
        {
            item.IsBusy = false;
            item.ProgressText = "";
            _anyBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
