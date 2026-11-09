using System.Text.Json;

namespace WinOptimizer.Infrastructure;

/// <summary>Пользовательские настройки приложения. Хранятся в settings.json рядом с остальными данными.</summary>
public sealed class AppSettings
{
    public static readonly int[] ScaleOptions = { 90, 100, 110, 125 };

    public const string StableChannel = "stable";
    public const string BetaChannel = "beta";

    /// <summary>Канал обновлений: «stable» (ветка main) или «beta» (ветка beta с новыми функциями).</summary>
    public string UpdateChannel { get; set; } = StableChannel;

    /// <summary>Показывать приветственное окно при запуске.</summary>
    public bool ShowWelcome { get; set; } = true;

    /// <summary>Предлагать создать точку восстановления перед системными твиками.</summary>
    public bool AskRestorePoint { get; set; } = true;

    /// <summary>Проверять обновления при запуске.</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

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

    public void Save()
    {
        Save(FilePath);
        Changed?.Invoke();
    }

    internal void Save(string path)
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Не удалось сохранить настройки", ex);
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
        if (UpdateChannel != StableChannel && UpdateChannel != BetaChannel) UpdateChannel = StableChannel;
    }

    internal void ResetToDefaults()
    {
        ShowWelcome = true;
        AskRestorePoint = true;
        CheckUpdatesOnStartup = true;
        Animations = true;
        UiScalePercent = 100;
        UpdateChannel = StableChannel;
    }
}
