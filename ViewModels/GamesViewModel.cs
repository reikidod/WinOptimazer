using System.Collections.ObjectModel;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

public sealed class GameItemViewModel : ObservableObject
{
    private bool _optiInstalled;
    private bool _highPerfGpu;
    private bool _busy;

    public GameItemViewModel(DiscoveredGame game, bool optiInstalled, bool highPerf)
    {
        Game = game;
        _optiInstalled = optiInstalled;
        _highPerfGpu = highPerf;
    }

    public DiscoveredGame Game { get; }
    public string Name => Game.Name;
    public string Platform => Game.Platform;
    public string Path => Game.ExePath;
    public string Folder => Game.InstallDir;

    public bool OptiInstalled
    {
        get => _optiInstalled;
        set
        {
            if (SetField(ref _optiInstalled, value))
                OnPropertyChanged(nameof(OptiStatus));
        }
    }

    public string OptiStatus => OptiInstalled ? "OptiScaler включён" : "OptiScaler не установлен";

    public bool HighPerfGpu
    {
        get => _highPerfGpu;
        set => SetField(ref _highPerfGpu, value);
    }

    public bool IsBusy
    {
        get => _busy;
        set => SetField(ref _busy, value);
    }

    private OptimizationResult? _result;

    /// <summary>Рекомендованные настройки (null — ещё не подбирали или скрыты).</summary>
    public OptimizationResult? Result
    {
        get => _result;
        set
        {
            if (!SetField(ref _result, value)) return;
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(HasWarnings));
            OnPropertyChanged(nameof(WarningsText));
            OnPropertyChanged(nameof(NotesText));
            OnPropertyChanged(nameof(AdviceButtonText));
        }
    }

    public bool HasResult => _result is not null;
    public bool HasWarnings => _result is { Warnings.Count: > 0 };
    public string WarningsText => _result is null ? "" : string.Join("\n", _result.Warnings.Select(w => "⚠ " + w));
    public string NotesText => _result is null ? "" : string.Join("\n", _result.Notes);
    public string AdviceButtonText => HasResult ? "Скрыть настройки" : "Подобрать настройки";
}

public sealed class GamesViewModel : ObservableObject
{
    private readonly IGameDiscoveryService _discovery;
    private readonly IOptiScalerService _opti;
    private readonly IDialogService _dialogs;
    private string _status = "Нажмите «Найти игры», чтобы просканировать Steam, Epic, GOG и Xbox.";
    private string _filter = "";
    private bool _busy;
    private readonly IHardwareProfileService _hardware;
    private readonly IBenchmarkService _benchmarks;
    private HardwareProfile? _profile;
    private HardwareScores? _scores;
    private string _hardwareSummary = "";

    public GamesViewModel(IGameDiscoveryService discovery, IOptiScalerService opti, IDialogService dialogs,
        IHardwareProfileService? hardware = null, IBenchmarkService? benchmarks = null)
    {
        _discovery = discovery;
        _opti = opti;
        _dialogs = dialogs;
        _hardware = hardware ?? new HardwareProfileService();
        _benchmarks = benchmarks ?? new BenchmarkService();
        OptimizeCommand = new AsyncRelayCommand<GameItemViewModel>(OptimizeAsync, _ => !IsBusy, Report);
        DetectHardwareCommand = new AsyncRelayCommand(async () =>
        {
            IsBusy = true;
            try { await EnsureHardwareAsync(force: true); }
            finally { IsBusy = false; }
        }, () => !IsBusy, Report);

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy, Report);
        InstallOptiCommand = new AsyncRelayCommand<GameItemViewModel>(InstallOptiAsync, _ => !IsBusy, Report);
        RemoveOptiCommand = new AsyncRelayCommand<GameItemViewModel>(RemoveOptiAsync, _ => !IsBusy, Report);
        ToggleGpuCommand = new RelayCommand<GameItemViewModel>(ToggleGpu);
        OpenFolderCommand = new RelayCommand<GameItemViewModel>(OpenFolder);
        DownloadOptiCommand = new AsyncRelayCommand(DownloadOptiAsync, () => !IsBusy, Report);
    }

    public ObservableCollection<GameItemViewModel> Games { get; } = new();

    public IEnumerable<GameItemViewModel> FilteredGames =>
        string.IsNullOrWhiteSpace(Filter)
            ? Games
            : Games.Where(g => g.Name.Contains(Filter, StringComparison.CurrentCultureIgnoreCase)
                               || g.Platform.Contains(Filter, StringComparison.CurrentCultureIgnoreCase));

    public ICommand ScanCommand { get; }
    public ICommand InstallOptiCommand { get; }
    public ICommand RemoveOptiCommand { get; }
    public ICommand ToggleGpuCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand DownloadOptiCommand { get; }
    public ICommand OptimizeCommand { get; }
    public ICommand DetectHardwareCommand { get; }

    public string HardwareSummary
    {
        get => _hardwareSummary;
        private set
        {
            if (SetField(ref _hardwareSummary, value)) OnPropertyChanged(nameof(HasHardware));
        }
    }

    public bool HasHardware => !string.IsNullOrEmpty(_hardwareSummary);

    private async Task EnsureHardwareAsync(bool force = false)
    {
        if (_profile is not null && _scores is not null && !force) return;
        Status = "Определяю железо и загружаю тесты комплектующих…";
        _profile = await _hardware.CollectAsync();
        _scores = await _benchmarks.GetScoresAsync(_profile, new Progress<string>(s => Status = s));
        HardwareSummary =
            $"Процессор: {_profile.CpuName} — {_scores.CpuSource}\n" +
            $"Видеокарта: {_profile.GpuName} ({_profile.VramGb:0.#} ГБ) — {_scores.GpuSource}\n" +
            $"Память: {_profile.RamGb:0.#} ГБ · Экран: {_profile.ScreenWidth}×{_profile.ScreenHeight} @ {_profile.RefreshHz} Гц";
        Status = "Железо определено. Нажмите «Подобрать настройки» у нужной игры.";
    }

    private async Task OptimizeAsync(GameItemViewModel? item)
    {
        if (item is null) return;
        if (item.HasResult)
        {
            item.Result = null;
            return;
        }

        IsBusy = true;
        item.IsBusy = true;
        try
        {
            await EnsureHardwareAsync();
            var traits = await Task.Run(() => GameKnowledge.Analyze(item.Name, item.Folder));
            item.Result = GameOptimizer.Recommend(traits, _profile!, _scores!);
            Status = $"Настройки для «{item.Name}» подобраны.";
        }
        finally
        {
            item.IsBusy = false;
            IsBusy = false;
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value))
                OnPropertyChanged(nameof(FilteredGames));
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetField(ref _busy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            Status = "Ищу установленные игры…";
            var found = await _discovery.ScanAsync();
            Games.Clear();
            foreach (var g in found)
            {
                var dir = Path.GetDirectoryName(g.ExePath) ?? g.InstallDir;
                Games.Add(new GameItemViewModel(
                    g,
                    _opti.IsInstalled(dir),
                    _opti.HasHighPerformanceGpu(g.ExePath)));
            }
            OnPropertyChanged(nameof(FilteredGames));
            Status = Games.Count == 0
                ? "Игры не найдены. Проверьте Steam/Epic или добавьте папку Games на диске."
                : $"Найдено игр: {Games.Count}. Можно поставить OptiScaler или режим высокой производительности GPU.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadOptiAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var (ok, msg) = await _opti.EnsureDownloadedAsync(progress);
            Status = msg;
            _dialogs.Info("OptiScaler", msg, ok ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallOptiAsync(GameItemViewModel? item)
    {
        if (item is null) return;
        if (!_dialogs.Confirm(
                "OptiScaler",
                $"Установить OptiScaler для «{item.Name}»?\n\n" +
                "Это замена апскейлера (DLSS/FSR/XeSS). Не ставьте в онлайн-игры с античитом (VAC, EAC, BattlEye) — возможен бан.\n" +
                "Рядом с игрой будет создан dxgi.dll и резервная копия."))
            return;

        IsBusy = true;
        item.IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var (ok, msg) = await _opti.InstallToGameAsync(item.Path, progress);
            if (ok) item.OptiInstalled = true;
            Status = msg;
            _dialogs.Info("OptiScaler", msg, ok ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            item.IsBusy = false;
            IsBusy = false;
        }
    }

    private async Task RemoveOptiAsync(GameItemViewModel? item)
    {
        if (item is null) return;
        IsBusy = true;
        item.IsBusy = true;
        try
        {
            var (ok, msg) = await _opti.UninstallFromGameAsync(item.Path);
            if (ok) item.OptiInstalled = false;
            Status = msg;
            _dialogs.Info("OptiScaler", msg, ok ? DialogKind.Success : DialogKind.Warning);
        }
        finally
        {
            item.IsBusy = false;
            IsBusy = false;
        }
    }

    private void ToggleGpu(GameItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            var enable = !item.HighPerfGpu;
            _opti.SetHighPerformanceGpu(item.Path, enable);
            item.HighPerfGpu = enable;
            Status = enable
                ? $"«{item.Name}»: видеокарта в режиме высокой производительности (безопасно)."
                : $"«{item.Name}»: режим GPU по умолчанию.";
        }
        catch (Exception ex)
        {
            _dialogs.Info("Графика", ex.Message, DialogKind.Warning);
        }
    }

    private static void OpenFolder(GameItemViewModel? item)
    {
        if (item is null || !Directory.Exists(item.Folder)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = item.Folder,
            UseShellExecute = true,
        });
    }

    private void Report(Exception ex)
    {
        Status = "Ошибка";
        _dialogs.Info("Игры", ex.Message, DialogKind.Warning);
    }
}
