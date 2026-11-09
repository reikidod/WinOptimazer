using WinOptimizer.Infrastructure;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "wo-settings-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Normalize_ClampsValuesAndRemovesInvalidTabs()
    {
        var settings = new AppSettings
        {
            UiScalePercent = 999,
            GamesTargetFps = -1,
            BackgroundBlurPercent = 120,
            BackgroundDimPercent = -20,
            CardOpacityPercent = 0,
            HiddenTabs = ["Apps", "Apps", "NotARealTab"],
            UpdateChannel = "unknown"
        };

        settings.Normalize();

        Assert.Equal(100, settings.UiScalePercent);
        Assert.Equal(0, settings.GamesTargetFps);
        Assert.Equal(100, settings.BackgroundBlurPercent);
        Assert.Equal(0, settings.BackgroundDimPercent);
        Assert.Equal(50, settings.CardOpacityPercent);
        Assert.Equal(new[] { "Apps" }, settings.HiddenTabs);
        Assert.Equal(AppSettings.StableChannel, settings.UpdateChannel);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsSettingsWithoutTemporaryFiles()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        var settings = new AppSettings
        {
            TransparencyEffectsEnabled = false,
            CardOpacityPercent = 73,
            BackgroundBlurPercent = 22,
            UpdateChannel = AppSettings.BetaChannel
        };

        settings.Save(path);
        var loaded = AppSettings.Load(path);

        Assert.False(loaded.TransparencyEffectsEnabled);
        Assert.Equal(73, loaded.CardOpacityPercent);
        Assert.Equal(22, loaded.BackgroundBlurPercent);
        Assert.Equal(AppSettings.BetaChannel, loaded.UpdateChannel);
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public void Load_CorruptedJsonReturnsSafeDefaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{ this is not valid JSON");

        var loaded = AppSettings.Load(path);

        Assert.Equal(100, loaded.UiScalePercent);
        Assert.Equal(40, loaded.BackgroundBlurPercent);
        Assert.True(loaded.TransparencyEffectsEnabled);
        Assert.Equal(AppSettings.StableChannel, loaded.UpdateChannel);
    }
}
