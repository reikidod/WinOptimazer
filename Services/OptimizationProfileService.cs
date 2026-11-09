using System.Text.Json;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

/// <summary>Профиль оптимизации: набор id твиков для пакетного применения.</summary>
public sealed record OptimizationProfile(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> TweakIds);

public interface IOptimizationProfileService
{
    IReadOnlyList<OptimizationProfile> BuiltIn { get; }
    IReadOnlyList<OptimizationProfile> UserProfiles { get; }
    void SaveUserProfile(OptimizationProfile profile);
    void DeleteUserProfile(string id);
    OptimizationProfile? Find(string id);
}

/// <summary>Встроенные и пользовательские профили. Не применяет твики сам — только хранит списки id.</summary>
public sealed class OptimizationProfileService : IOptimizationProfileService
{
    private readonly string _path;
    private List<OptimizationProfile> _user = new();

    public OptimizationProfileService(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "profiles.json");
        Load();
    }

    public IReadOnlyList<OptimizationProfile> BuiltIn { get; } =
    [
        new("gaming", "Игровой",
            "Отключает Game DVR, ускорение мыши и сетевой throttling; повышает приоритет игр. Без опасных служб.",
            ["game-dvr-off", "fso-off", "mouse-accel-off", "mmcss-games", "network-throttling-off", "system-responsiveness-10"]),
        new("daily", "Повседневный",
            "Убирает виджеты, рекламу в поиске и лишние анимации. Безопасный набор для обычной работы.",
            ["widgets-off", "search-highlights-off", "animations-off", "sticky-keys-off", "transparency-off"]),
        new("power-save", "Энергосбережение",
            "Минимум визуальных эффектов и фоновых подсказок. Схему питания не меняет автоматически.",
            ["transparency-off", "animations-off", "widgets-off", "search-highlights-off"]),
    ];

    public IReadOnlyList<OptimizationProfile> UserProfiles
    {
        get { lock (_user) return _user.ToList(); }
    }

    public OptimizationProfile? Find(string id)
    {
        var built = BuiltIn.FirstOrDefault(p => p.Id == id);
        if (built is not null) return built;
        lock (_user) return _user.FirstOrDefault(p => p.Id == id);
    }

    public void SaveUserProfile(OptimizationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id)) throw new ArgumentException("Id required", nameof(profile));
        lock (_user)
        {
            _user.RemoveAll(p => p.Id == profile.Id);
            _user.Add(profile);
            SaveUnlocked();
        }
    }

    public void DeleteUserProfile(string id)
    {
        lock (_user)
        {
            _user.RemoveAll(p => p.Id == id);
            SaveUnlocked();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<OptimizationProfile>>(File.ReadAllText(_path));
            if (list is null) return;
            lock (_user)
            {
                _user = list.Where(p => !string.IsNullOrWhiteSpace(p.Id)).ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Error("Не удалось загрузить профили", ex);
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_user, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Не удалось сохранить профили", ex);
        }
    }
}
