using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;

namespace WinOptimizer.ViewModels;

/// <summary>Плитка выбора темы: образец фона, карточки и акцента.</summary>
public sealed class ThemeOption : ObservableObject
{
    private bool _selected;

    public ThemeOption(ThemeInfo info)
    {
        Id = info.Id;
        Name = info.Name;
        BgBrush = Make(info.Palette.BgWindow);
        CardBrush = Make(info.Palette.BgCard);
        AccentBrush = Make(info.Palette.Accent);
        TextBrush = Make(info.Palette.TextPrimary);
    }

    public string Id { get; }
    public string Name { get; }
    public Brush BgBrush { get; }
    public Brush CardBrush { get; }
    public Brush AccentBrush { get; }
    public Brush TextBrush { get; }

    public bool IsSelected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    internal static Brush Make(string hex)
    {
        var b = new SolidColorBrush(ThemeManager.ParseColor(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>Чип выбора акцентного цвета.</summary>
public sealed class AccentOption : ObservableObject
{
    private bool _selected;

    public AccentOption(AccentInfo info)
    {
        Id = info.Id;
        Name = info.Name;
        Swatch = info.Accent.Length > 0 ? ThemeOption.Make(info.Accent) : ThemeOption.Make("#8C93A3");
    }

    public string Id { get; }
    public string Name { get; }
    public Brush Swatch { get; }

    public bool IsSelected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }
}

/// <summary>Страница «Настройки»: поведение приложения, внешний вид, обслуживание данных.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private string _status = "";

    public SettingsViewModel() : this(AppSettings.Current) { }

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        SetScaleCommand = new RelayCommand<string>(p =>
        {
            if (int.TryParse(p, out var percent)) UiScalePercent = percent;
        });
        OpenLogCommand = new RelayCommand(() => OpenPath(AppPaths.LogFile, selectFile: true));
        OpenDataFolderCommand = new RelayCommand(() => OpenPath(AppPaths.DataDirectory, selectFile: false));
        ClearCacheCommand = new RelayCommand(ClearCache);
        ResetCommand = new RelayCommand(Reset);
        RefreshCacheSize();
        InitAppearance();
        _ = LoadAutostartAsync();
    }

    private bool _startWithWindows;

    /// <summary>Автозапуск хранится не в settings.json, а в планировщике Windows: берём реальное состояние оттуда.</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            _startWithWindows = value;
            OnPropertyChanged();
            _ = ApplyAutostartAsync(value);
        }
    }

    public bool UseBetaChannel
    {
        get => _settings.UpdateChannel == AppSettings.BetaChannel;
        set
        {
            if (UseBetaChannel == value) return;
            _settings.UpdateChannel = value ? AppSettings.BetaChannel : AppSettings.StableChannel;
            Commit(nameof(UseBetaChannel));
            Status = value
                ? "Канал: бета. Нажмите «Проверить обновления», чтобы получить свежую бету."
                : "Канал: стабильный. Если установлена бета, она предложит вернуться на стабильную версию.";
        }
    }

    public bool ShowTweaksTab
    {
        get => _settings.IsTabVisible("Tweaks");
        set { if (_settings.IsTabVisible("Tweaks") != value) { _settings.SetTabVisible("Tweaks", value); Commit(nameof(ShowTweaksTab)); } }
    }

    public bool ShowDebloatTab
    {
        get => _settings.IsTabVisible("Debloat");
        set { if (_settings.IsTabVisible("Debloat") != value) { _settings.SetTabVisible("Debloat", value); Commit(nameof(ShowDebloatTab)); } }
    }

    public bool ShowAppsTab
    {
        get => _settings.IsTabVisible("Apps");
        set { if (_settings.IsTabVisible("Apps") != value) { _settings.SetTabVisible("Apps", value); Commit(nameof(ShowAppsTab)); } }
    }

    public bool ShowStoreTab
    {
        get => _settings.IsTabVisible("Store");
        set { if (_settings.IsTabVisible("Store") != value) { _settings.SetTabVisible("Store", value); Commit(nameof(ShowStoreTab)); } }
    }

    public bool ShowGamesTab
    {
        get => _settings.IsTabVisible("Games");
        set { if (_settings.IsTabVisible("Games") != value) { _settings.SetTabVisible("Games", value); Commit(nameof(ShowGamesTab)); } }
    }

    public bool ShowSystemTab
    {
        get => _settings.IsTabVisible("System");
        set { if (_settings.IsTabVisible("System") != value) { _settings.SetTabVisible("System", value); Commit(nameof(ShowSystemTab)); } }
    }

    public bool ShowMaintenanceTab
    {
        get => _settings.IsTabVisible("Maintenance");
        set { if (_settings.IsTabVisible("Maintenance") != value) { _settings.SetTabVisible("Maintenance", value); Commit(nameof(ShowMaintenanceTab)); } }
    }

    public bool AskRestorePoint
    {
        get => _settings.AskRestorePoint;
        set { if (_settings.AskRestorePoint != value) { _settings.AskRestorePoint = value; Commit(nameof(AskRestorePoint)); } }
    }

    private async Task LoadAutostartAsync()
    {
        _startWithWindows = await Task.Run(AutostartService.IsEnabled);
        OnPropertyChanged(nameof(StartWithWindows));
    }

    private async Task ApplyAutostartAsync(bool enable)
    {
        string error = "";
        var ok = await Task.Run(() => AutostartService.Set(enable, out error));
        if (ok)
        {
            Status = enable ? "Автозапуск включён: приложение откроется после входа в Windows." : "Автозапуск выключен.";
            return;
        }

        _startWithWindows = !enable;
        OnPropertyChanged(nameof(StartWithWindows));
        Status = "Не удалось изменить автозапуск: " + error;
    }

    public ICommand SetScaleCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand ResetCommand { get; }

    public bool ShowWelcome
    {
        get => _settings.ShowWelcome;
        set { if (_settings.ShowWelcome != value) { _settings.ShowWelcome = value; Commit(nameof(ShowWelcome)); } }
    }

    public bool CheckUpdatesOnStartup
    {
        get => _settings.CheckUpdatesOnStartup;
        set { if (_settings.CheckUpdatesOnStartup != value) { _settings.CheckUpdatesOnStartup = value; Commit(nameof(CheckUpdatesOnStartup)); } }
    }

    public bool Animations
    {
        get => _settings.Animations;
        set { if (_settings.Animations != value) { _settings.Animations = value; Commit(nameof(Animations)); } }
    }

    public int UiScalePercent
    {
        get => _settings.UiScalePercent;
        set
        {
            if (_settings.UiScalePercent == value || !AppSettings.ScaleOptions.Contains(value)) return;
            _settings.UiScalePercent = value;
            Commit(nameof(UiScalePercent));
            OnPropertyChanged(nameof(ScaleText));
        }
    }

    public string ScaleText => $"Сейчас: {UiScalePercent}%";

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    private string _cacheText = "";

    public string CacheText
    {
        get => _cacheText;
        private set => SetField(ref _cacheText, value);
    }

    // ---------- Тема, акцент, фон ----------

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = ThemeManager.Themes.Select(t => new ThemeOption(t)).ToList();
    public IReadOnlyList<AccentOption> AccentOptions { get; } = ThemeManager.Accents.Select(a => new AccentOption(a)).ToList();

    public ICommand SetThemeCommand { get; private set; } = null!;
    public ICommand SetAccentCommand { get; private set; } = null!;
    public ICommand PickBackgroundCommand { get; private set; } = null!;
    public ICommand ClearBackgroundCommand { get; private set; } = null!;

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    private void InitAppearance()
    {
        SetThemeCommand = new RelayCommand<string>(id =>
        {
            if (id is null || !ThemeManager.IsKnownTheme(id) || _settings.ThemeId == id) return;
            _settings.ThemeId = id;
            RefreshSelection();
            _settings.Save();
        });
        SetAccentCommand = new RelayCommand<string>(id =>
        {
            if (id is null || !ThemeManager.IsKnownAccent(id) || _settings.AccentId == id) return;
            _settings.AccentId = id;
            RefreshSelection();
            _settings.Save();
        });
        PickBackgroundCommand = new RelayCommand(PickBackground);
        ClearBackgroundCommand = new RelayCommand(ClearBackground, () => HasBackground);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var t in ThemeOptions) t.IsSelected = t.Id == _settings.ThemeId;
        foreach (var a in AccentOptions) a.IsSelected = a.Id == _settings.AccentId;
    }

    public bool HasBackground => !string.IsNullOrEmpty(_settings.BackgroundImage);

    public string BackgroundName => HasBackground ? Path.GetFileName(_settings.BackgroundImage) : "Фон не выбран";

    public int BackgroundBlur
    {
        get => _settings.BackgroundBlurPercent;
        set => SetLive(_settings.BackgroundBlurPercent, Math.Clamp(value, 0, 100), v => _settings.BackgroundBlurPercent = v, nameof(BackgroundBlur));
    }

    public int BackgroundDim
    {
        get => _settings.BackgroundDimPercent;
        set => SetLive(_settings.BackgroundDimPercent, Math.Clamp(value, 0, 90), v => _settings.BackgroundDimPercent = v, nameof(BackgroundDim));
    }

    public int CardOpacity
    {
        get => _settings.CardOpacityPercent;
        set => SetLive(_settings.CardOpacityPercent, Math.Clamp(value, 50, 100), v => _settings.CardOpacityPercent = v, nameof(CardOpacity));
    }

    /// <summary>Ползунок меняет значение десятки раз в секунду: применяем сразу, а на диск пишем один раз после паузы.</summary>
    private void SetLive(int current, int value, Action<int> assign, string property)
    {
        if (current == value) return;
        assign(value);
        OnPropertyChanged(property);
        AppSettings.RaiseChanged();
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick -= OnSaveTimer;
        _saveTimer.Tick += OnSaveTimer;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimer(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        _settings.Save();
    }

    private void PickBackground()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите картинку для фона",
            Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var info = new FileInfo(dialog.FileName);
            // 15 МБ достаточно для фона: большие фото тормозят BlurEffect.
            if (info.Length > 15L * 1024 * 1024)
            {
                Status = "Файл слишком большой: выберите картинку до 15 МБ.";
                return;
            }

            // Проверяем, что файл действительно читается как изображение, до копирования.
            var probe = new BitmapImage();
            probe.BeginInit();
            probe.CacheOption = BitmapCacheOption.OnLoad;
            probe.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            probe.DecodePixelWidth = 64;
            probe.UriSource = new Uri(dialog.FileName);
            probe.EndInit();
            probe.Freeze();

            // Одна копия с фиксированным именем — не копятся background-*.
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var ext = info.Extension.ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp")) ext = ".jpg";
            var dest = Path.Combine(AppPaths.DataDirectory, "background" + ext);
            DeleteBackgroundFiles(keep: null);
            File.Copy(dialog.FileName, dest, overwrite: true);

            _settings.BackgroundImage = dest;
            _settings.Save();
            OnPropertyChanged(nameof(HasBackground));
            OnPropertyChanged(nameof(BackgroundName));
            CommandManager.InvalidateRequerySuggested();
            Status = "Фон установлен. Размытие и затемнение настраиваются ползунками.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            Logger.Error("Не удалось установить фон", ex);
            Status = "Не удалось установить фон: файл не читается как картинка.";
        }
    }

    private void ClearBackground()
    {
        _settings.BackgroundImage = "";
        _settings.Save();
        DeleteBackgroundFiles(keep: null);
        OnPropertyChanged(nameof(HasBackground));
        OnPropertyChanged(nameof(BackgroundName));
        CommandManager.InvalidateRequerySuggested();
        Status = "Фон убран.";
    }

    private static void DeleteBackgroundFiles(string? keep)
    {
        try
        {
            foreach (var pattern in new[] { "background-*", "background.png", "background.jpg", "background.jpeg", "background.bmp" })
            {
                foreach (var f in Directory.EnumerateFiles(AppPaths.DataDirectory, pattern))
                    if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                        File.Delete(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Не удалось удалить старый фон", ex);
        }
    }

    private void Commit(string property)
    {
        OnPropertyChanged(property);
        _settings.Save();
    }

    private void Reset()
    {
        _settings.ResetToDefaults();
        _settings.Save();
        foreach (var p in new[] { nameof(ShowWelcome), nameof(AskRestorePoint), nameof(UseBetaChannel), nameof(ShowTweaksTab), nameof(ShowDebloatTab), nameof(ShowAppsTab), nameof(ShowStoreTab), nameof(ShowGamesTab), nameof(ShowSystemTab), nameof(ShowMaintenanceTab), nameof(CheckUpdatesOnStartup), nameof(Animations), nameof(UiScalePercent), nameof(ScaleText), nameof(BackgroundBlur), nameof(BackgroundDim), nameof(CardOpacity), nameof(HasBackground), nameof(BackgroundName) })
            OnPropertyChanged(p);
        DeleteBackgroundFiles(keep: null);
        RefreshSelection();
        Status = "Настройки сброшены к значениям по умолчанию.";
    }

    // ---------- Кэш ----------

    private static IEnumerable<string> CacheFolders() => new[]
    {
        Path.Combine(AppPaths.DataDirectory, "store-cache"),
        Path.Combine(AppPaths.DataDirectory, "benchmarks"),
        AppPaths.DownloadsDirectory,
    };

    internal static long CacheSizeBytes()
    {
        long total = 0;
        foreach (var dir in CacheFolders())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    total += new FileInfo(f).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* пропускаем недоступное */ }
        }

        return total;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} МБ",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} ГБ",
    };

    private void RefreshCacheSize() => CacheText = $"Занято: {FormatSize(CacheSizeBytes())} (иконки Store, таблицы производительности, скачанные установщики)";

    private void ClearCache()
    {
        var failed = 0;
        foreach (var dir in CacheFolders())
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                Logger.Error("Не удалось очистить кэш: " + dir, ex);
            }
        }

        RefreshCacheSize();
        Status = failed == 0 ? "Кэш очищен." : $"Кэш очищен не полностью: {failed} папк. заняты другим процессом.";
    }

    private void OpenPath(string path, bool selectFile)
    {
        try
        {
            if (selectFile && File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
            {
                Directory.CreateDirectory(selectFile ? AppPaths.DataDirectory : path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{(selectFile ? AppPaths.DataDirectory : path)}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Status = "Не удалось открыть папку: " + ex.Message;
        }
    }
}
