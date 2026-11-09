using System.Diagnostics;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;

namespace WinOptimizer.ViewModels;

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

    private void Commit(string property)
    {
        OnPropertyChanged(property);
        _settings.Save();
    }

    private void Reset()
    {
        _settings.ResetToDefaults();
        _settings.Save();
        foreach (var p in new[] { nameof(ShowWelcome), nameof(AskRestorePoint), nameof(UseBetaChannel), nameof(ShowTweaksTab), nameof(ShowDebloatTab), nameof(ShowAppsTab), nameof(ShowStoreTab), nameof(ShowGamesTab), nameof(ShowSystemTab), nameof(ShowMaintenanceTab), nameof(CheckUpdatesOnStartup), nameof(Animations), nameof(UiScalePercent), nameof(ScaleText) })
            OnPropertyChanged(p);
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
