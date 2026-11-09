using System.Text.Json;

namespace WinOptimizer.Services;

public sealed record OptimizationProfile(
    int SchemaVersion,
    string Name,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> SelectedTweakIds);

/// <summary>
/// Импорт/экспорт набора выбранных твиков. Профиль только меняет выделение в интерфейсе:
/// применение системных настроек всегда остаётся отдельным действием пользователя.
/// </summary>
public static class OptimizationProfileService
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static OptimizationProfile Create(string name, IEnumerable<string> selectedTweakIds) =>
        new(
            CurrentSchemaVersion,
            string.IsNullOrWhiteSpace(name) ? "Мой профиль" : name.Trim(),
            DateTimeOffset.Now,
            selectedTweakIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray());

    public static void Save(string path, OptimizationProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profile);
        Validate(profile);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, JsonOptions));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static OptimizationProfile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize<OptimizationProfile>(json)
                      ?? throw new InvalidDataException("Файл профиля пуст или имеет неверный формат.");
        Validate(profile);
        return profile;
    }

    private static void Validate(OptimizationProfile profile)
    {
        if (profile.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Версия профиля {profile.SchemaVersion} не поддерживается.");
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidDataException("У профиля отсутствует название.");
        if (profile.SelectedTweakIds is null)
            throw new InvalidDataException("В профиле отсутствует список твиков.");
    }
}
