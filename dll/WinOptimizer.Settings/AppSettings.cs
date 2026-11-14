using System.Text.Json;
using System.Reflection;

namespace WinOptimizer.Infrastructure;

/// <summary>Пользовательские настройки приложения. Хранятся в settings.json рядом с остальными данными.</summary>
public sealed class AppSettings
{
    public static readonly int[] TargetFpsOptions = { 0, 30, 60, 90, 120, 144, 240 };
    public static readonly int[] ScaleOptions = { 90, 100, 110, 125 };

    /// <summary>Вкладки, которые можно скрыть (имена элементов AppPage). «Настройки» скрыть нельзя.</summary>
    public static readonly string[] HideableTabs = { "Tweaks", "Debloat", "Apps", "Store", "Games", "System", "Maintenance" };

    /// <summary>Скрытые пользователем вкладки боковой панели.</summary>
    public List<string> HiddenTabs { get; set; } = new();

    /// <summary>Боковая панель скрыта пользователем (Ctrl+B или кнопка «‹»); состояние запоминается между запусками.</summary>
    public bool SidebarHidden { get; set; }

    public bool IsTabVisible(string tab) => !HiddenTabs.Contains(tab);

    public void SetTabVisible(string tab, bool visible)
    {
        if (!HideableTabs.Contains(tab)) return;
        HiddenTabs.RemoveAll(t => t == tab);
        if (!visible) HiddenTabs.Add(tab);
    }

    /// <summary>Тема оформления (см. ThemeManager.Themes).</summary>
    public string ThemeId { get; set; } = ThemeManager.DefaultTheme;

    /// <summary>Акцентный цвет: «theme» — из темы, иначе blue/red/green/purple/orange/pink.</summary>
    public string AccentId { get; set; } = ThemeManager.ThemeAccent;

    /// <summary>Путь к скопированной в папку данных картинке фона; пусто — без фона.</summary>
    public string BackgroundImage { get; set; } = "";

    /// <summary>Размытие фона, 0–100 %.</summary>
    public int BackgroundBlurPercent { get; set; } = 40;

    /// <summary>Затемнение фона, 0–90 %: чем больше, тем лучше читается текст.</summary>
    public int BackgroundDimPercent { get; set; } = 45;

    /// <summary>Включить прозрачность модулей и боковой панели поверх пользовательского фона.</summary>
    public bool TransparencyEffectsEnabled { get; set; } = true;

    /// <summary>Непрозрачность модулей поверх фона, 50–100 %.</summary>
    public int CardOpacityPercent { get; set; } = 88;

    public const string StableChannel = "stable";
    public const string BetaChannel = "beta";

    /// <summary>Канал обновлений: beta-сборка по умолчанию следит за beta; стабильная и dev-сборки — за stable.</summary>
    public string UpdateChannel { get; set; } = GetDefaultUpdateChannel();

    private static string GetDefaultUpdateChannel()
    {
        var label = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var metadata = label.IndexOf('+');
        if (metadata >= 0) label = label[..metadata];
        return label.EndsWith("-beta", StringComparison.OrdinalIgnoreCase) ? BetaChannel : StableChannel;
    }

    /// <summary>Показывать приветственное окно при запуске.</summary>
    public bool ShowWelcome { get; set; } = true;

    /// <summary>Предлагать создать точку восстановления перед системными твиками.</summary>
    public bool AskRestorePoint { get; set; } = true;

    /// <summary>Проверять обновления при запуске.</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>Целевой FPS для подбора настроек игр: 0 — по частоте монитора.</summary>
    public int GamesTargetFps { get; set; }

    /// <summary>Персональные настройки Game Boost для каждой найденной игры.</summary>
    public List<GameBoostGameSetting> GameBoostGames { get; set; } = new();

    /// <summary>Плавные анимации интерфейса (дополнительно к системной настройке Windows).</summary>
    public bool Animations { get; set; } = true;

    /// <summary>Масштаб интерфейса в процентах (90–125).</summary>
    public int UiScalePercent { get; set; } = 100;

    public double UiScale => UiScalePercent / 100.0;

    private static AppSettings? _current;

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static AppSettings Current => _current ??= Load(FilePath);

    /// <summary>Вызывается после сохранения: окна применяют масштаб и анимации сразу.</summary>
    public static event Action? Changed;

    /// <summary>Сообщает окнам о новых значениях без записи на диск (для ползунков: запись делается позже одним разом).</summary>
    public static void RaiseChanged() => Changed?.Invoke();

    public bool Save()
    {
        if (!Save(FilePath))
            return false;

        Changed?.Invoke();
        return true;
    }

    internal bool Save(string path)
    {
        string? temporaryPath = null;
        try
        {
            Normalize();

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Сначала записываем полный JSON во временный файл рядом с settings.json.
            // Если приложение завершится во время записи, предыдущие настройки останутся целыми.
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json);

            // Замена в пределах одного тома: читатели увидят либо старый, либо новый файл,
            // но не частично записанный JSON.
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Не удалось сохранить настройки", ex);
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Error("Не удалось удалить временный файл настроек", ex);
                }
            }
        }
    }

    internal static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
                s.Normalize();
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Error("Настройки повреждены, используются значения по умолчанию", ex);
        }

        return new AppSettings();
    }

    /// <summary>Приводит значения к допустимым: битый файл не должен ломать интерфейс.</summary>
    internal void Normalize()
    {
        if (!ScaleOptions.Contains(UiScalePercent)) UiScalePercent = 100;
        HiddenTabs = (HiddenTabs ?? new()).Where(HideableTabs.Contains).Distinct().ToList();
        if (!TargetFpsOptions.Contains(GamesTargetFps)) GamesTargetFps = 0;
        GameBoostGames = (GameBoostGames ?? new List<GameBoostGameSetting>())
            .Where(profile => profile is not null && !string.IsNullOrWhiteSpace(profile.ExecutablePath) &&
                              Path.IsPathFullyQualified(profile.ExecutablePath))
            .Select(profile =>
            {
                string normalizedPath;
                try { normalizedPath = Path.GetFullPath(profile.ExecutablePath); }
                catch { normalizedPath = ""; }
                return new GameBoostGameSetting
                {
                    ExecutablePath = normalizedPath,
                    Enabled = profile.Enabled,
                    Mode = profile.Mode is "maximum" ? "maximum" : "balanced",
                    PowerPlanId = Guid.TryParse(profile.PowerPlanId, out var powerPlanId)
                        ? powerPlanId.ToString("D")
                        : ""
                };
            })
            .Where(profile => !string.IsNullOrWhiteSpace(profile.ExecutablePath))
            .GroupBy(profile => profile.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        if (!ThemeManager.IsKnownTheme(ThemeId)) ThemeId = ThemeManager.DefaultTheme;
        if (!ThemeManager.IsKnownAccent(AccentId)) AccentId = ThemeManager.ThemeAccent;
        BackgroundBlurPercent = Math.Clamp(BackgroundBlurPercent, 0, 100);
        BackgroundDimPercent = Math.Clamp(BackgroundDimPercent, 0, 90);
        CardOpacityPercent = Math.Clamp(CardOpacityPercent, 50, 100);
        // Файл фона могли удалить вручную: тогда просто работаем без фона.
        if (!string.IsNullOrEmpty(BackgroundImage) && !File.Exists(BackgroundImage)) BackgroundImage = "";
        if (UpdateChannel != StableChannel && UpdateChannel != BetaChannel) UpdateChannel = GetDefaultUpdateChannel();
    }

    internal void ResetToDefaults()
    {
        ShowWelcome = true;
        AskRestorePoint = true;
        CheckUpdatesOnStartup = true;
        Animations = true;
        UiScalePercent = 100;
        UpdateChannel = GetDefaultUpdateChannel();
        HiddenTabs.Clear();
        GamesTargetFps = 0;
        ThemeId = ThemeManager.DefaultTheme;
        AccentId = ThemeManager.ThemeAccent;
        BackgroundImage = "";
        BackgroundBlurPercent = 40;
        BackgroundDimPercent = 45;
        CardOpacityPercent = 88;
        TransparencyEffectsEnabled = true;
    }
}

/// <summary>
/// Per-game Game Boost preference. The service receives only executable paths, enabled state and a safe priority/power profile.
/// </summary>
public sealed class GameBoostGameSetting
{
    public string ExecutablePath { get; set; } = "";
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "balanced";
    /// <summary>Windows power scheme GUID. Empty means do not change the active scheme.</summary>
    public string PowerPlanId { get; set; } = "";
}
