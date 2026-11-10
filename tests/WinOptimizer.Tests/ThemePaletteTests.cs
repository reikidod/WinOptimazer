using WinOptimizer.Infrastructure;
using Xunit;

namespace WinOptimizer.Tests;

public class ThemePaletteTests
{
    public static IEnumerable<object[]> Palettes =>
        ThemeManager.Themes.Select(theme => new object[] { theme.Id, theme.Palette });

    [Theory]
    [MemberData(nameof(Palettes))]
    public void ThemePalette_HasReadableTextContrast(string themeId, ThemePalette palette)
    {
        Assert.True(
            ThemeManager.Contrast(palette.TextPrimary, palette.BgCard) >= 4.5,
            $"Primary text contrast is too low for theme '{themeId}'.");

        Assert.True(
            ThemeManager.Contrast(palette.TextSecondary, palette.BgCard) >= 4.5,
            $"Secondary text contrast is too low for theme '{themeId}'.");
    }

    [Fact]
    public void ThemeAndAccentIdentifiers_AreUnique()
    {
        Assert.Equal(
            ThemeManager.Themes.Count,
            ThemeManager.Themes.Select(theme => theme.Id).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(
            ThemeManager.Accents.Count,
            ThemeManager.Accents.Select(accent => accent.Id).Distinct(StringComparer.Ordinal).Count());
    }
}
