using System.Collections.ObjectModel;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

public sealed record GameBoostModeOption(string Id, string Label);
public sealed record GameBoostPowerPlanOption(string Id, string Label);

public sealed class GameItemViewModel : ObservableObject
{
    private bool _optiInstalled;
    private bool _highPerfGpu;
    private bool _busy;
    private bool _gameBoostEnabled;
    private string _gameBoostMode;
    private string _gameBoostPowerPlanId;
    private readonly Action<GameItemViewModel>? _profileChanged;

    public GameItemViewModel(DiscoveredGame game, bool optiInstalled, bool highPerf,
        bool gameBoostEnabled = false, string gameBoostMode = "balanced", string gameBoostPowerPlanId = "",
        Action<GameItemViewModel>? profileChanged = null)
    {
        Game = game;
        _optiInstalled = optiInstalled;
        _highPerfGpu = highPerf;
        _gameBoostEnabled = gameBoostEnabled;
        _gameBoostMode = gameBoostMode == "maximum" ? "maximum" : "balanced";
        _gameBoostPowerPlanId = Guid.TryParse(gameBoostPowerPlanId, out var powerPlanId) && powerPlanId != Guid.Empty
            ? powerPlanId.ToString("D")
            : "";
        _profileChanged = profileChanged;
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

    /// <summary>Включает или исключает именно эту игру из списка, отслеживаемого службой.</summary>
    public bool GameBoostEnabled
    {
        get => _gameBoostEnabled;
        set
        {
            if (SetField(ref _gameBoostEnabled, value))
                _profileChanged?.Invoke(this);
        }
    }

    /// <summary>balanced — AboveNormal; maximum — High + временный план High Performance.</summary>
    public string GameBoostMode
    {
        get => _gameBoostMode;
        set
        {
            var normalized = value == "maximum" ? "maximum" : "balanced";
            if (SetField(ref _gameBoostMode, normalized))
            {
                OnPropertyChanged(nameof(GameBoostModeDescription));
                _profileChanged?.Invoke(this);
            }
        }
    }

    public string GameBoostModeDescription => GameBoostMode == "maximum"
        ? "Приоритет High + временный системный план «Высокая производительность». Возможен рост энергопотребления и температуры; защита Windows не отключается."
        : "Приоритет Above Normal без смены системного плана питания.";

    /// <summary>Empty means that Game Boost must keep the current Windows power plan.</summary>
    public string GameBoostPowerPlanId
    {
        get => _gameBoostPowerPlanId;
        set
        {
            var normalized = Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("D") : "";
            if (SetField(ref _gameBoostPowerPlanId, normalized))
                _profileChanged?.Invoke(this);
        }
    }

    private OptimizationResult? _result;

    /// <summary>Что известно об игре; нужно, чтобы пересчитать настройки при смене целевого FPS.</summary>
    public GameTraits? Traits { get; set; }

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
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly SemaphoreSlim _profileSyncGate = new(1, 1);
    private HardwareProfile? _profile;
    private HardwareScores? _scores;
    private string _hardwareSummary = "";
    private readonly GameBoostClient _gameBoost = new();
    private string _boostStatus = "Проверяю состояние службы…";
    private bool _gameBoostInstalled;
    private bool _gameBoostRunning;
    private bool _gameBoostActive;
    private int _configuredGameCount;
    private int _activeGameCount;
    private IReadOnlyList<GameBoostPowerPlanOption> _gameBoostPowerPlanOptions =
        [new("", "Не менять текущий план")];

    public GamesViewModel(IGameDiscoveryService discovery, IOptiScalerService opti, IDialogService dialogs,
        IHardwareProfileService? hardware = null, IBenchmarkService? benchmarks = null)
    {
        _discovery = discovery;
        _opti = opti;
        _dialogs = dialogs;
        _hardware = hardware ?? new HardwareProfileService();
        _benchmarks = benchmarks ?? new BenchmarkService();
        LoadPowerPlanOptions();
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
        InstallGameBoostServiceCommand = new AsyncRelayCommand(InstallGameBoostServiceAsync, () => !IsBusy, Report);
        RemoveGameBoostServiceCommand = new AsyncRelayCommand(RemoveGameBoostServiceAsync, () => !IsBusy, Report);
        SyncGameBoostGamesCommand = new AsyncRelayCommand(SyncGameBoostGamesAsync, () => !IsBusy && GameBoostInstalled && GameBoostRunning, Report);
        RefreshGameBoostStatusCommand = new AsyncRelayCommand(RefreshGameBoostStatusAsync, () => !IsBusy, Report);
        _ = RefreshGameBoostStatusAsync();
    }

    public ObservableCollection<GameItemViewModel> Games { get; } = new();
    public bool HasGames => Games.Count > 0;
    public string GameCountText => Games.Count == 0 ? "Игры ещё не найдены" : $"Найдено игр: {Games.Count}";

    public IEnumerable<GameItemViewModel> FilteredGames =>
        string.IsNullOrWhiteSpace(Filter)
            ? Games
            : Games.Where(g => g.Name.Contains(Filter, StringComparison.CurrentCultureIgnoreCase)
                               || g.Platform.Contains(Filter, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Варианты целевого FPS: 0 — подобрать по частоте монитора.</summary>
    public static readonly int[] TargetFpsValues = AppSettings.TargetFpsOptions;

    public IReadOnlyList<string> TargetFpsLabels { get; } =
        TargetFpsValues.Select(v => v == 0 ? "Авто (по монитору)" : v + " FPS").ToArray();

    private int _targetFpsIndex = Math.Max(0, Array.IndexOf(TargetFpsValues, AppSettings.Current.GamesTargetFps));

    public int TargetFpsIndex
    {
        get => _targetFpsIndex;
        set
        {
            if (value < 0 || value >= TargetFpsValues.Length) return;
            if (!SetField(ref _targetFpsIndex, value)) return;
            AppSettings.Current.GamesTargetFps = TargetFpsValues[value];
            AppSettings.Current.Save();
            RecalculateAll();
        }
    }

    public int TargetFps => TargetFpsValues[_targetFpsIndex];

    /// <summary>Пересчитывает уже показанные рекомендации под новый целевой FPS без повторной загрузки тестов.</summary>
    private void RecalculateAll()
    {
        if (_profile is null || _scores is null) return;
        foreach (var g in Games.Where(g => g.HasResult && g.Traits is not null))
            g.Result = GameOptimizer.Recommend(g.Traits!, _profile, _scores, TargetFps);
    }

    public ICommand ScanCommand { get; }
    public ICommand InstallOptiCommand { get; }
    public ICommand RemoveOptiCommand { get; }
    public ICommand ToggleGpuCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand DownloadOptiCommand { get; }
    public ICommand OptimizeCommand { get; }
    public ICommand DetectHardwareCommand { get; }
    public ICommand InstallGameBoostServiceCommand { get; }
    public ICommand RemoveGameBoostServiceCommand { get; }
    public ICommand SyncGameBoostGamesCommand { get; }
    public ICommand RefreshGameBoostStatusCommand { get; }

    public string GameBoostStatus { get => _boostStatus; private set => SetField(ref _boostStatus, value); }
    public bool GameBoostInstalled { get => _gameBoostInstalled; private set { if (SetField(ref _gameBoostInstalled, value)) OnPropertyChanged(nameof(GameBoostNeedsInstall)); } }
    public bool GameBoostRunning { get => _gameBoostRunning; private set { if (SetField(ref _gameBoostRunning, value)) OnPropertyChanged(nameof(GameBoostNeedsInstall)); } }
    public bool GameBoostNeedsInstall => !GameBoostInstalled || !GameBoostRunning;
    public bool GameBoostActive { get => _gameBoostActive; private set => SetField(ref _gameBoostActive, value); }
    public int ConfiguredGameCount { get => _configuredGameCount; private set => SetField(ref _configuredGameCount, value); }
    public int ActiveGameCount { get => _activeGameCount; private set => SetField(ref _activeGameCount, value); }
    public int EnabledGameCount => Games.Count(game => game.GameBoostEnabled);

    public IReadOnlyList<GameBoostModeOption> GameBoostModeOptions { get; } =
    [
        new("balanced", "Сбалансированный"),
        new("maximum", "Максимальная производительность")
    ];

    public IReadOnlyList<GameBoostPowerPlanOption> GameBoostPowerPlanOptions
    {
        get => _gameBoostPowerPlanOptions;
        private set => SetField(ref _gameBoostPowerPlanOptions, value);
    }

    private void LoadPowerPlanOptions()
    {
        try
        {
            var options = new List<GameBoostPowerPlanOption>
            {
                new("", "Не менять текущий план")
            };

            var plans = PowerPlanCatalog.GetAvailablePlans();
            options.AddRange(plans.Select(plan => new GameBoostPowerPlanOption(
                plan.Id,
                plan.Name + (plan.IsActive ? " · сейчас активен" : ""))));

            // Retain an explicit unavailable entry for a plan removed from Windows,
            // rather than silently changing the user's saved preference.
            foreach (var savedId in _settings.GameBoostGames
                         .Select(profile => profile.PowerPlanId)
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!plans.Any(plan => string.Equals(plan.Id, savedId, StringComparison.OrdinalIgnoreCase)))
                    options.Add(new GameBoostPowerPlanOption(savedId, "Недоступен в Windows · " + savedId[..8]));
            }

            GameBoostPowerPlanOptions = options;
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось получить список планов питания Windows", ex);
            GameBoostStatus = "Не удалось загрузить планы питания: " + ex.Message;
        }
    }

    private async Task RefreshGameBoostStatusAsync()
    {
        try
        {
            GameBoostStatus = "Проверяю состояние службы…";
            var status = await _gameBoost.GetStatusAsync();
            GameBoostInstalled = status.IsInstalled;
            GameBoostRunning = status.IsRunning;
            GameBoostActive = status.IsActive;
            ConfiguredGameCount = status.ConfiguredGames;
            ActiveGameCount = status.ActiveGameCount;
            GameBoostStatus = status.Message;
        }
        catch (Exception ex)
        {
            GameBoostRunning = false;
            GameBoostActive = false;
            ActiveGameCount = 0;
            GameBoostStatus = "Не удалось проверить службу: " + ex.Message;
            Logger.Error("Game Boost status check failed", ex);
        }
    }

    private async Task InstallGameBoostServiceAsync()
    {
        IsBusy = true;
        try
        {
            GameBoostStatus = "Запрашиваю права администратора для установки службы…";
            var result = await _gameBoost.InstallAsync();
            await RefreshGameBoostStatusAsync();
            if (result.Success)
            {
                GameBoostStatus = result.Message;
                await SyncGameBoostGamesAsync();
            }
            else
            {
                // Keep the installer diagnostics visible in the panel after refreshing SCM state.
                GameBoostStatus = result.Message;
                _dialogs.Info("Game Boost", result.Message, DialogKind.Warning);
            }
        }
        finally { IsBusy = false; }
    }

    private async Task RemoveGameBoostServiceAsync()
    {
        if (!_dialogs.Confirm("Game Boost", "Удалить службу Game Boost? Автоматическая оптимизация игр будет отключена.")) return;
        IsBusy = true;
        try
        {
            var result = await _gameBoost.UninstallAsync();
            GameBoostStatus = result.Message;
            if (!result.Success) _dialogs.Info("Game Boost", result.Message, DialogKind.Warning);
            await RefreshGameBoostStatusAsync();
        }
        finally { IsBusy = false; }
    }

    private void OnGameBoostProfileChanged(GameItemViewModel item)
    {
        var saved = _settings.GameBoostGames.FirstOrDefault(profile =>
            string.Equals(profile.ExecutablePath, item.Path, StringComparison.OrdinalIgnoreCase));

        if (saved is null)
        {
            saved = new GameBoostGameSetting { ExecutablePath = Path.GetFullPath(item.Path) };
            _settings.GameBoostGames.Add(saved);
        }

        saved.Enabled = item.GameBoostEnabled;
        saved.Mode = item.GameBoostMode;
        saved.PowerPlanId = item.GameBoostPowerPlanId;

        if (!_settings.Save())
        {
            GameBoostStatus = "Не удалось сохранить игровой профиль. Изменения пока не отправлены службе.";
            return;
        }

        OnPropertyChanged(nameof(EnabledGameCount));
        ConfiguredGameCount = _settings.GameBoostGames.Count(profile => profile.Enabled);
        if (GameBoostInstalled && GameBoostRunning)
            _ = SyncGameBoostGamesAsync();
    }

    private async Task SyncGameBoostGamesAsync()
    {
        await _profileSyncGate.WaitAsync();
        try
        {
            var profiles = _settings.GameBoostGames
                .Select(profile => new GameBoostProfile(
                    profile.ExecutablePath, profile.Enabled,
                    profile.Mode == "maximum" ? "maximum" : "balanced",
                    profile.PowerPlanId))
                .ToArray();

            var result = await _gameBoost.ConfigureAsync(profiles);
            GameBoostStatus = result.Message;
            if (result.Success)
            {
                ConfiguredGameCount = result.Count;
                GameBoostActive = result.Active;
            }
            else
            {
                _dialogs.Info("Game Boost", result.Message, DialogKind.Warning);
            }
        }
        finally
        {
            _profileSyncGate.Release();
        }
    }

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
            item.Traits = traits;
            item.Result = GameOptimizer.Recommend(traits, _profile!, _scores!, TargetFps);
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
                var savedProfile = _settings.GameBoostGames.FirstOrDefault(profile =>
                    string.Equals(profile.ExecutablePath, g.ExePath, StringComparison.OrdinalIgnoreCase));
                Games.Add(new GameItemViewModel(
                    g,
                    _opti.IsInstalled(dir),
                    _opti.HasHighPerformanceGpu(g.ExePath),
                    savedProfile?.Enabled ?? false,
                    savedProfile?.Mode ?? "balanced",
                    savedProfile?.PowerPlanId ?? "",
                    OnGameBoostProfileChanged));
            }
            OnPropertyChanged(nameof(FilteredGames));
            OnPropertyChanged(nameof(HasGames));
            OnPropertyChanged(nameof(GameCountText));
            OnPropertyChanged(nameof(EnabledGameCount));
            if (GameBoostInstalled && GameBoostRunning) await SyncGameBoostGamesAsync();
            else if (GameBoostInstalled) GameBoostStatus = "Служба остановлена. Нажмите «Установить службу» для восстановления.";
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
