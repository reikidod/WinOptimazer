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
    public const string DefaultTheme = "aurora";
    public const string ThemeAccent = "theme";

    public static readonly IReadOnlyList<ThemeInfo> Themes =
    [
        // WinOptimizer Signal Control Center: глубокий уголь + акцент логотипа #00C58E.
        // ID aurora сохранён, чтобы не сбрасывать выбор темы у существующих установок.
        new("aurora", "Control Center (Signal)", new("#101418", "#151A1F", "#1A2127", "#222A31", "#2C363E", "#3D4A54", "#00C58E", "#00A577", "#2A343C",
            "#F2F5F3", "#B5C0BA", "#8E9B94", "#2FD49A", "#D6AD6A", "#D97878", "#97A39B")),
        new("dark", "Тёмная", new("#1B1C1F", "#202225", "#2A2C31", "#353840", "#3A3D44", "#7F8CA3", "#8AAAD8", "#4B6CA3", "#3A3D44",
            "#F2F3F5", "#C2C6CE", "#A9AEB9", "#7FB38F", "#D2AE6D", "#EE8B8B", "#9AA0AB")),
        new("light", "Светлая", new("#F6FAFF", "#FFFFFF", "#FFFFFF", "#EAF5FF", "#DCEBFA", "#72C9F0", "#009BD8", "#0078D4", "#DCEBFA",
            "#1E293B", "#475569", "#64748B", "#10B981", "#D97706", "#DC2626", "#64748B", IsLight: true)),
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
        new("green", "Зелёный", "#00C58E", "#00A577"),
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
            var cardAlpha = hasBackground && s.TransparencyEffectsEnabled
                ? (byte)Math.Round(255 * s.CardOpacityPercent / 100.0)
                : (byte)255;

            var theme = Find(s.ThemeId);
            var p = theme.Palette;
            var accentInfo = Accents.FirstOrDefault(a => a.Id == s.AccentId) ?? Accents[0];
            var accent = accentInfo.Id == ThemeAccent ? p.Accent : accentInfo.Accent;
            var accentStrong = accentInfo.Id == ThemeAccent ? p.AccentStrong : accentInfo.AccentStrong;

            var key = $"{theme.Id}|{s.AccentId}|{cardAlpha}|{s.TransparencyEffectsEnabled}";
            if (_appliedKey == key) return;
            _appliedKey = key;

            Set(app, "BgWindowBrush", p.BgWindow);
            Set(app, "BgSidebarBrush", p.BgSidebar);
            Set(app, "BgCardBrush", p.BgCard, cardAlpha);
            Set(app, "BgCardHoverBrush", p.BgCardHover, cardAlpha);
            Set(app, "BgInputBrush", p.BgWindow);
            Set(app, "BorderBrush", p.Border);
            Set(app, "BorderHoverBrush", p.BorderHover);
            Set(app, "AccentBrush", accent);
            Set(app, "AccentStrongBrush", accentStrong);
            Set(app, "AccentSoftBrush", accent, 38);
            Set(app, "TrackOffBrush", p.TrackOff);
            Set(app, "TextPrimaryBrush", p.TextPrimary);
            Set(app, "TextSecondaryBrush", p.TextSecondary);
            Set(app, "TextDimBrush", p.TextDim);
            Set(app, "SuccessBrush", p.Success);
            Set(app, "WarnBrush", p.Warn);
            Set(app, "DangerBrush", p.Danger);
            Set(app, "NeutralBrush", p.Neutral);
            Set(app, "TextOnAccentBrush", "#FFFFFF");
            Set(app, "BrandCyanBrush", accent);
            Set(app, "BrandRedBrush", accentStrong);
            Set(app, "BrandActionBrush", accent);

            SetThemeAtmosphere(app, accent, accentStrong, p.IsLight);
        }
        catch
        {
            // Не роняем приложение из‑за сбоя темы.
        }
    }

    private static void SetThemeAtmosphere(Application app, string accent, string accentStrong, bool isLight)
    {
        var primary = ParseColor(accent);
        var secondary = ParseColor(accentStrong);
        var gridColor = isLight ? Color.FromArgb(14, 20, 28, 40) : Color.FromArgb(13, 255, 255, 255);
        var grid = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 56, 56),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 56, 56),
            ViewboxUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing(new SolidColorBrush(gridColor), null,
                Geometry.Parse("M0,0 H56 V1 H0 Z M0,0 V56 H1 V0 Z"))
        };
        grid.Freeze();
        app.Resources["SiteGridBrush"] = grid;
        app.Resources["SiteCyanGlowBrush"] = CreateGlow(primary);
        app.Resources["SiteRedGlowBrush"] = CreateGlow(primary);
        app.Resources["SiteBlueGlowBrush"] = CreateGlow(secondary);
        // На тёмных темах конец градиента смешиваем с насыщенным акцентом, чтобы белый текст кнопки читался.
        app.Resources["SiteAccentButtonBrush"] = CreateGradient(secondary, isLight ? primary : Mix(secondary, primary, 0.12));
        app.Resources["BrandGradientBrush"] = CreateGradient(primary, secondary);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static RadialGradientBrush CreateGlow(Color color)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(42, color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateGradient(Color start, Color end)
    {
        var brush = new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static void Set(Application app, string key, string hex, byte alpha = 255)
    {
        var color = ParseColor(hex);
        if (alpha < 255) color = Color.FromArgb(alpha, color.R, color.G, color.B);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    public static Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8)
            return Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16));
        return Color.FromRgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    /// <summary>Контраст двух цветов по WCAG (для тестов палитры).</summary>
    public static double Contrast(string hexA, string hexB)
    {
        static double Lum(Color c)
        {
            static double F(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
        }

        var a = Lum(ParseColor(hexA));
        var b = Lum(ParseColor(hexB));
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
