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
    public void Normalize_GameBoostProfilesAreCanonicalizedAndDeduplicated()
    {
        var settings = new AppSettings
        {
            GameBoostGames =
            [
                new GameBoostGameSetting { ExecutablePath = Path.Combine(Path.GetTempPath(), "GameA.exe"), Enabled = true, Mode = "maximum" },
                new GameBoostGameSetting { ExecutablePath = Path.Combine(Path.GetTempPath(), "gamea.exe"), Enabled = false, Mode = "invalid" },
                new GameBoostGameSetting { ExecutablePath = "relative\\game.exe", Enabled = true, Mode = "maximum" }
            ]
        };

        settings.Normalize();

        var profile = Assert.Single(settings.GameBoostGames);
        Assert.False(profile.Enabled);
        Assert.Equal("balanced", profile.Mode);
        Assert.True(Path.IsPathFullyQualified(profile.ExecutablePath));
    }

    [Fact]
    public void SaveAndLoad_PreservesPerGameBoostProfiles()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        var executable = Path.Combine(_directory, "sample-game.exe");
        File.WriteAllText(executable, "placeholder");
        var settings = new AppSettings
        {
            GameBoostGames =
            [
                new GameBoostGameSetting
                {
                    ExecutablePath = executable,
                    Enabled = true,
                    Mode = "maximum",
                    PowerPlanId = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"
                }
            ]
        };

        Assert.True(settings.Save(path));
        var loaded = AppSettings.Load(path);

        var profile = Assert.Single(loaded.GameBoostGames);
        Assert.Equal(executable, profile.ExecutablePath);
        Assert.True(profile.Enabled);
        Assert.Equal("maximum", profile.Mode);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", profile.PowerPlanId);
    }

    [Fact]
    public void Normalize_RejectsInvalidPerGamePowerPlanGuid()
    {
        var settings = new AppSettings
        {
            GameBoostGames =
            [
                new GameBoostGameSetting
                {
                    ExecutablePath = Path.Combine(Path.GetTempPath(), "Sample.exe"),
                    PowerPlanId = "not-a-guid"
                }
            ]
        };

        settings.Normalize();

        Assert.Equal("", Assert.Single(settings.GameBoostGames).PowerPlanId);
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

        Assert.True(settings.Save(path));
        var loaded = AppSettings.Load(path);

        Assert.False(loaded.TransparencyEffectsEnabled);
        Assert.Equal(73, loaded.CardOpacityPercent);
        Assert.Equal(22, loaded.BackgroundBlurPercent);
        Assert.Equal(AppSettings.BetaChannel, loaded.UpdateChannel);
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_FailurePreservesExistingDirectoryAndCleansTemporaryFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(path);
        var markerPath = Path.Combine(path, "keep.txt");
        File.WriteAllText(markerPath, "previous data");

        var saved = new AppSettings { UiScalePercent = 125 }.Save(path);

        Assert.False(saved);
        Assert.Equal("previous data", File.ReadAllText(markerPath));
        Assert.Empty(Directory.GetFiles(_directory));
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
