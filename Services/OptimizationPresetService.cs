namespace WinOptimizer.Tweaks;

/// <summary>Готовые наборы безопасных пользовательских оптимизаций.</summary>
public enum OptimizationPresetLevel
{
    Light,
    Medium,
    Aggressive,
}

public sealed record OptimizationPreset(
    string Id,
    string Title,
    string Subtitle,
    string Description,
    string Warning,
    IReadOnlyList<string> TweakIds);

/// <summary>
/// Наборы намеренно не включают отключение Defender, брандмауэра, Windows Update,
/// VBS/HVCI и защитных механизмов CPU. Такие изменения остаются только в ручном каталоге.
/// </summary>
public static class OptimizationPresetService
{
    private static readonly string[] LightIds =
    [
        "animations-off",
        "transparency-off",
        "widgets-off",
        "search-highlights-off",
    ];

    private static readonly string[] MediumIds =
    [
        .. LightIds,
        "game-dvr-off",
        "background-apps-off",
        "chrome-edge-background-off",
        "toast-notifications-off",
    ];

    private static readonly string[] AggressiveIds =
    [
        .. MediumIds,
        "power-high-performance",
        "disable-delivery-optimization",
        "store-autodownload-off",
        "maps-autoupdate-off",
        "feedback-prompts-off",
        "autoplay-off",
    ];

    public static IReadOnlyList<OptimizationPreset> All { get; } =
    [
        new OptimizationPreset(
            "light",
            "Лёгкая",
            "Минимальные изменения",
            "Убирает часть визуальных эффектов и необязательные элементы интерфейса. Подходит для повседневной работы; заметный прирост FPS не обещается.",
            "Изменения в основном визуальные. Если анимации, прозрачность или виджеты нужны — оставьте ручной выбор.",
            LightIds),
        new OptimizationPreset(
            "medium",
            "Средняя",
            "Меньше фоновой активности",
            "Дополнительно отключает фоновую запись игр, фоновые приложения Store и работу браузеров после закрытия. Уведомления можно вернуть вручную.",
            "Xbox Game Bar не сможет записывать клипы в фоне; приложения Store и браузеры будут меньше работать в фоне.",
            MediumIds),
        new OptimizationPreset(
            "aggressive",
            "Агрессивная",
            "Максимум выбранных фоновых ограничений",
            "Расширяет средний набор: включает высокую производительность питания и ограничивает фоновую доставку обновлений, автообновления Store и офлайн-карт.",
            "Выше энергопотребление и нагрев на ноутбуках; обновления приложений Store и карт придётся запускать вручную. Windows Update и системная защита остаются включены.",
            AggressiveIds),
    ];

    public static OptimizationPreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
