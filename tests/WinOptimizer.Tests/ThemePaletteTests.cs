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
    public void LightTheme_UsesApprovedImageThreePalette()
    {
        var palette = ThemeManager.Find("light").Palette;

        Assert.Equal("#F6FAFF", palette.BgWindow);
        Assert.Equal("#FFFFFF", palette.BgSidebar);
        Assert.Equal("#FFFFFF", palette.BgCard);
        Assert.Equal("#EAF5FF", palette.BgCardHover);
        Assert.Equal("#DCEBFA", palette.Border);
        Assert.Equal("#72C9F0", palette.BorderHover);
        Assert.Equal("#009BD8", palette.Accent);
        Assert.Equal("#0078D4", palette.AccentStrong);
        Assert.Equal("#1E293B", palette.TextPrimary);
        Assert.Equal("#475569", palette.TextSecondary);
        Assert.Equal("#64748B", palette.TextDim);
        Assert.True(palette.IsLight);
    }

    [Fact]
    public void ControlCenterTheme_IsDefaultAndUsesTerminalPalette()
    {
        Assert.Equal("aurora", ThemeManager.DefaultTheme);
        Assert.Equal("aurora", ThemeManager.Themes[0].Id);

        var palette = ThemeManager.Find("aurora").Palette;
        Assert.Equal("#0D1117", palette.BgWindow);
        Assert.Equal("#161B22", palette.BgCard);
        Assert.Equal("#3FB950", palette.Accent);
        Assert.Equal("#1F6F3D", palette.AccentStrong);
        Assert.False(palette.IsLight);
        Assert.True(ThemeManager.Contrast(palette.TextDim, palette.BgCard) >= 4.5);
        Assert.True(ThemeManager.Contrast("#FFFFFF", "#1F6F3D") >= 4.5);
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
