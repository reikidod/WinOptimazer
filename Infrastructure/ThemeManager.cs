using System.Windows;
using System.Windows.Media;

namespace WinOptimizer.Infrastructure;

/// <summary>Цвета одной темы оформления. Все значения — #RRGGBB.</summary>
public sealed record ThemePalette(
    string BgWindow, string BgSidebar, string BgCard, string BgCardHover,
    string Border, string BorderHover, string Accent, string AccentStrong, string TrackOff,
    string TextPrimary, string TextSecondary, string TextDim,
    string Success, string Warn, string Danger, string Neutral,
    bool IsLight = false);

public sealed record ThemeInfo(string Id, string Name, ThemePalette Palette);

public sealed record AccentInfo(string Id, string Name, string Accent, string AccentStrong);

/// <summary>
/// Темы и акцентные цвета. Кисти в приложении подключены через DynamicResource, поэтому смена темы —
/// это подмена кистей в Application.Resources: все окна перекрашиваются сразу, без перезапуска.
/// </summary>
public static class ThemeManager
{
    public const string DefaultTheme = "dark";
    public const string ThemeAccent = "theme";

    public static readonly IReadOnlyList<ThemeInfo> Themes =
    [
        new("dark", "Тёмная", new("#1B1C1F", "#202225", "#2A2C31", "#353840", "#3A3D44", "#7F8CA3", "#8AAAD8", "#4B6CA3", "#3A3D44",
            "#F2F3F5", "#C2C6CE", "#A9AEB9", "#7FB38F", "#D2AE6D", "#EE8B8B", "#9AA0AB")),
        new("light", "Светлая", new("#F3F4F7", "#FFFFFF", "#FFFFFF", "#EEF1F7", "#D8DCE5", "#8A94A8", "#2F6FDB", "#2F6FDB", "#C9CEDA",
            "#1B1E24", "#4A5160", "#666D7E", "#2E7D50", "#9A6412", "#C23B3B", "#6B7280", IsLight: true)),
        new("redblack", "Красно-чёрная", new("#120C0C", "#1A1010", "#231516", "#2F1B1D", "#43262A", "#A04048", "#FF5A5F", "#C0262D", "#43262A",
            "#F5EDED", "#D0BFBF", "#B09A9A", "#7FB38F", "#D2AE6D", "#FF8A8A", "#A09090")),
        new("midnight", "Ночной синий", new("#0E1422", "#121A2C", "#18223A", "#1F2C4A", "#2A3A5C", "#6F8FD0", "#6EA8FF", "#3A66C8", "#2A3A5C",
            "#EEF2FA", "#BCC8DE", "#9AA8C4", "#7FB38F", "#D2AE6D", "#EE8B8B", "#9AA0AB")),
        new("emerald", "Изумрудная", new("#0F1A16", "#13211C", "#1A2B25", "#223830", "#2E4A40", "#6FB59A", "#4FD1A1", "#1F8F6A", "#2E4A40",
            "#ECF5F1", "#BBD0C7", "#98B0A5", "#7FD3A0", "#D2AE6D", "#EE8B8B", "#9AA8A0")),
        new("violet", "Фиолетовая", new("#17121F", "#1C1626", "#261D33", "#33264A", "#43315F", "#A184D6", "#B48CFF", "#7B4FD0", "#43315F",
            "#F3EEFA", "#CDC2E0", "#A99BC4", "#7FB38F", "#D2AE6D", "#EE8B8B", "#A59DB0")),
        new("amoled", "AMOLED (чёрная)", new("#000000", "#0A0A0A", "#141414", "#1E1E1E", "#2A2A2A", "#7A7A7A", "#8AAAD8", "#4B6CA3", "#2A2A2A",
            "#F2F2F2", "#BDBDBD", "#9E9E9E", "#7FB38F", "#D2AE6D", "#EE8B8B", "#9E9E9E")),
    ];

    public static readonly IReadOnlyList<AccentInfo> Accents =
    [
        new(ThemeAccent, "Как в теме", "", ""),
        new("blue", "Синий", "#6EA8FF", "#3A66C8"),
        new("red", "Красный", "#FF6B6B", "#C0392B"),
        new("green", "Зелёный", "#4FD1A1", "#1F8F6A"),
        new("purple", "Фиолетовый", "#B48CFF", "#7B4FD0"),
        new("orange", "Оранжевый", "#FFA94D", "#C46A12"),
        new("pink", "Розовый", "#FF7EB6", "#C23C7E"),
    ];

    private static bool _initialized;
    private static string? _appliedKey;

    public static ThemeInfo Find(string? id) => Themes.FirstOrDefault(t => t.Id == id) ?? Themes[0];

    public static bool IsKnownTheme(string? id) => Themes.Any(t => t.Id == id);

    public static bool IsKnownAccent(string? id) => Accents.Any(a => a.Id == id);

    /// <summary>Подписывается на изменения настроек и применяет тему. Вызывается один раз при запуске.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Apply(AppSettings.Current);
        AppSettings.Changed += () => Apply(AppSettings.Current);
    }

    /// <summary>Подменяет кисти приложения под выбранную тему и акцент. Безопасно вызывать из UI-потока.</summary>
    public static void Apply(AppSettings s)
    {
        var app = Application.Current;
        if (app is null) return;
        try
        {
            // Размытие/затемнение фона не меняют кисти — не пересоздаём их на каждый тик ползунка.
            var hasBackground = !string.IsNullOrEmpty(s.BackgroundImage);
            var cardAlpha = hasBackground ? (byte)Math.Round(255 * s.CardOpacityPercent / 100.0) : (byte)255;
            var themeKey = $"{s.ThemeId}|{s.AccentId}|{hasBackground}";
            var key = $"{themeKey}|{cardAlpha}";
            if (key == _appliedKey) return;

            var p = Find(s.ThemeId).Palette;
            // Быстрый путь: двигается только «Плотность карточек» — обновляем alpha у 3 кистей, без полного пересоздания палитры.
            if (_appliedKey is not null && _appliedKey.StartsWith(themeKey + "|", StringComparison.Ordinal))
            {
                Set(app, "BgSidebarBrush", p.BgSidebar, cardAlpha);
                Set(app, "BgCardBrush", p.BgCard, cardAlpha);
                Set(app, "BgCardHoverBrush", p.BgCardHover, cardAlpha);
                _appliedKey = key;
                return;
            }
            _appliedKey = key;

            var accent = Accents.FirstOrDefault(a => a.Id == s.AccentId);
            var accentHex = p.Accent;
            var accentStrong = p.AccentStrong;
            if (accent is { Id: not ThemeAccent } && accent.Accent.Length > 0)
            {
                // На светлой теме пастельный акцент плохо читается на белом: берём насыщенный вариант и для текста.
                accentHex = p.IsLight ? accent.AccentStrong : accent.Accent;
                accentStrong = accent.AccentStrong;
            }

            Set(app, "BgWindowBrush", p.BgWindow);
            Set(app, "BgSidebarBrush", p.BgSidebar, cardAlpha);
            Set(app, "BgCardBrush", p.BgCard, cardAlpha);
            Set(app, "BgCardHoverBrush", p.BgCardHover, cardAlpha);
            Set(app, "BorderBrush", p.Border);
            Set(app, "BorderHoverBrush", p.BorderHover);
            Set(app, "AccentBrush", accentHex);
            Set(app, "AccentStrongBrush", accentStrong);
            Set(app, "TrackOffBrush", p.TrackOff);
            Set(app, "TextPrimaryBrush", p.TextPrimary);
            Set(app, "TextSecondaryBrush", p.TextSecondary);
            Set(app, "TextDimBrush", p.TextDim);
            Set(app, "SuccessBrush", p.Success);
            Set(app, "WarnBrush", p.Warn);
            Set(app, "DangerBrush", p.Danger);
            Set(app, "NeutralBrush", p.Neutral);
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось применить тему", ex);
        }
    }

    private static void Set(Application app, string key, string hex, byte alpha = 255)
    {
        var c = ParseColor(hex);
        c.A = alpha;
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    internal static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Контраст двух цветов по WCAG (1…21). Нужен для проверки читаемости палитр в тестах.</summary>
    internal static double Contrast(string hexA, string hexB)
    {
        static double Lin(byte v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Lum(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        var la = Lum(ParseColor(hexA));
        var lb = Lum(ParseColor(hexB));
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }
}
