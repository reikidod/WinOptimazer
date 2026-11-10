using System.Security.Cryptography;
using Microsoft.Win32;
using WinOptimizer.Services;
using WinOptimizer.Tweaks;
using Xunit;

namespace WinOptimizer.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v0.4.12", "0.4.12")]
    [InlineData("0.4.12", "0.4.12")]
    [InlineData("V1.2.3-beta", "1.2.3")]
    public void NormalizeVersion_StripsPrefixAndSuffix(string tag, string expected) =>
        Assert.Equal(expected, UpdateService.NormalizeVersion(tag));

    [Theory]
    [InlineData("0.3.9", "0.4.1", -1)]
    [InlineData("0.4.10", "0.4.9", 1)]
    [InlineData("0.4.0", "0.4", 0)]
    public void CompareVersions_IsNumeric(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(UpdateService.CompareVersions(a, b)));

    [Fact]
    public void DisplayVersion_IsNotEmpty() => Assert.False(string.IsNullOrWhiteSpace(new UpdateService().DisplayVersion));
}

public class DebloatNamingTests
{
    [Theory]
    [InlineData("Microsoft.WindowsCalculator", "Калькулятор")]
    [InlineData("Microsoft.BingWeather", "Погода")]
    public void FriendlyName_UsesKnownNames(string package, string expected) =>
        Assert.Equal(expected, DebloatService.FriendlyName(package));

    [Fact]
    public void FriendlyName_LanguagePack() =>
        Assert.StartsWith("Языковой пакет", DebloatService.FriendlyName("Microsoft.LanguageExperiencePackru-RU"));

    [Fact]
    public void FriendlyName_UnknownPackage_IsReadable() =>
        Assert.Equal("Some Cool App", DebloatService.FriendlyName("Microsoft.SomeCoolApp"));

    [Fact]
    public void Publisher_IsParsedFromSubject() =>
        Assert.Equal("Microsoft Corporation",
            DebloatService.PublisherFromSubject("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
}

public class BackupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* не критично */ }
    }

    [Fact]
    public void SaveGetRemove_RoundTrips()
    {
        var path = Path.Combine(_dir, "backup.json");
        var store = new BackupStore(path);
        store.Save("t", [new SavedValue("HKCU\\X", "V", "DWord", "5", true)]);
        var again = new BackupStore(path).Get("t");
        Assert.NotNull(again);
        Assert.Equal("5", again![0].Data);
        store.Remove("t");
        Assert.Null(new BackupStore(path).Get("t"));
    }

    [Fact]
    public void CorruptFile_IsQuarantined_NotCrash()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "backup.json");
        File.WriteAllText(path, "{ это не json");
        var store = new BackupStore(path);
        Assert.Null(store.Get("anything"));
        Assert.Contains(Directory.GetFiles(_dir), f => f.Contains(".corrupt-"));
    }
}

public class RegistryTweakTests : IDisposable
{
    private const string Root = @"Software\WinOptimizerTests";
    private readonly string _sub = Root + "\\" + Guid.NewGuid().ToString("N");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
        try { Directory.Delete(_dir, true); } catch { /* не критично */ }
    }

    private RegistryTweak Make(params RegistryEntry[] entries) =>
        new(new TweakInfo("test", "t", "d", EvidenceLevel.NoData, "", false), entries, new BackupStore(Path.Combine(_dir, "b.json")));

    [Fact]
    public void Apply_ThenRevert_RestoresOriginalValues()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_sub))
            k.SetValue("A", 5, RegistryValueKind.DWord);

        var tweak = Make(
            new RegistryEntry(RegistryHive.CurrentUser, _sub, "A", RegistryValueKind.DWord, 1, 0),
            new RegistryEntry(RegistryHive.CurrentUser, _sub, "B", RegistryValueKind.String, "x", null));

        Assert.False(tweak.IsApplied());
        tweak.Apply();
        Assert.True(tweak.IsApplied());

        tweak.Revert();
        using var key = Registry.CurrentUser.OpenSubKey(_sub)!;
        Assert.Equal(5, key.GetValue("A"));          // вернулось исходное, а не «по умолчанию»
        Assert.Null(key.GetValue("B"));              // значения не было — значит удалено
        Assert.False(tweak.IsApplied());
    }

    [Fact]
    public void Apply_Twice_DoesNotLoseOriginal()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_sub))
            k.SetValue("A", 7, RegistryValueKind.DWord);

        var tweak = Make(new RegistryEntry(RegistryHive.CurrentUser, _sub, "A", RegistryValueKind.DWord, 1, 0));
        tweak.Apply();
        tweak.Apply();
        tweak.Revert();

        using var key = Registry.CurrentUser.OpenSubKey(_sub)!;
        Assert.Equal(7, key.GetValue("A"));
    }
}

public class CatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* не критично */ }
    }

    [Fact]
    public void Catalog_IsConsistent()
    {
        var tweaks = TweakCatalog.Create(new BackupStore(Path.Combine(_dir, "b.json")));
        Assert.NotEmpty(tweaks);

        var ids = tweaks.Select(t => t.Info.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        foreach (var t in tweaks)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Info.Title), t.Info.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Info.Description), t.Info.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Info.Category), t.Info.Id);
            Assert.NotEqual("Category", t.Info.Category);   // регрессия: твик попадал в категорию «Category»
        }
    }

    [Fact]
    public void Catalog_IsAppliedDoesNotThrow()
    {
        foreach (var t in TweakCatalog.Create(new BackupStore(Path.Combine(_dir, "b.json"))))
        {
            var ex = Record.Exception(() => t.IsApplied());
            Assert.Null(ex);
        }
    }
}

public class RecommendedFilesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WinOptimizer.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден.");
    }

    [Fact]
    public void EveryRecommendedFile_ExistsAndMatchesPinnedHash()
    {
        var folder = Path.Combine(RepoRoot(), "recommended");
        foreach (var f in new RecommendedFilesService().Files)
        {
            var path = Path.Combine(folder, f.FileName);
            Assert.True(File.Exists(path), $"Нет файла recommended/{f.FileName}");
            using var fs = File.OpenRead(path);
            Assert.Equal(f.Sha256, Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant(), ignoreCase: true);
        }
    }

    [Fact]
    public void RecommendedIds_AreUnique()
    {
        var ids = new RecommendedFilesService().Files.Select(f => f.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}

public class SystemInfoTests
{
    [Fact]
    public void Collect_DoesNotThrow()
    {
        var sections = new SystemInfoService().CollectAsync().GetAwaiter().GetResult();
        Assert.NotEmpty(sections);
        // Раздел, который не удалось собрать, содержит строку «Ошибка»: регрессия «Видеокарта» падала на закрытом разделе реестра.
        foreach (var section in sections)
            Assert.DoesNotContain(section.Lines, l => l.Label == "Ошибка" && section.Title == "Видеокарта");
    }
}

public class GameOptimizerTests
{
    private static HardwareProfile Hw(string gpu = "NVIDIA GeForce RTX 3060", double vram = 12, int w = 1920, int h = 1080, int hz = 144, double ram = 16) =>
        new("CPU", 6, 12, 4.4, ram, gpu, vram, w, h, hz);

    private static GameTraits Game(GameClass c, bool dlss = false, bool fsr = false, bool fg = false) =>
        new("Test", c, "тест", dlss, fg, fsr, false, false, null, null);

    private static HardwareScores Scores(double gpu, double cpu = 20000) => new(gpu, "тест", cpu, "тест");

    [Fact]
    public void StrongGpu_LightGame_GetsUltraWithoutUpscaling()
    {
        var r = GameOptimizer.Recommend(Game(GameClass.Light), Hw("NVIDIA GeForce RTX 4090", 24), Scores(38000), 144);
        Assert.Equal("Ультра", r.Preset);
        Assert.Equal("Выкл", r.Upscaler);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void WeakGpu_HeavyGame_UsesUpscalingAndLowPreset()
    {
        var r = GameOptimizer.Recommend(Game(GameClass.Heavy, dlss: true), Hw("NVIDIA GeForce GTX 1050 Ti", 4, hz: 60), Scores(6300), 60);
        Assert.NotEqual("Ультра", r.Preset);
        Assert.NotEqual("Выкл", r.Upscaler);
    }

    [Fact]
    public void BelowMinimum_WarnsAboutTarget()
    {
        var r = GameOptimizer.Recommend(Game(GameClass.VeryHeavy), Hw("Intel(R) UHD Graphics 630", 1, hz: 60), Scores(1500), 60);
        Assert.Contains(r.Warnings, w => w.Contains("встроенной графике"));
        Assert.Contains(r.Warnings, w => w.Contains("ниже требований"));
    }

    [Fact]
    public void SlowCpu_WarnsAboutBottleneck()
    {
        var r = GameOptimizer.Recommend(Game(GameClass.Heavy), Hw(), Scores(17000, cpu: 8000), 120);
        Assert.Contains(r.Warnings, w => w.Contains("Процессор слабоват"));
    }

    [Fact]
    public void FrameGeneration_OnlyWhenSupportedAndNeeded()
    {
        var weak = GameOptimizer.Recommend(Game(GameClass.Heavy, dlss: true, fg: true),
            Hw("NVIDIA GeForce RTX 4060", 8, hz: 144), Scores(19700), 144);
        Assert.True(weak.FrameGeneration);

        var none = GameOptimizer.Recommend(Game(GameClass.Heavy, dlss: true, fg: false),
            Hw("NVIDIA GeForce RTX 4060", 8, hz: 144), Scores(19700), 144);
        Assert.False(none.FrameGeneration);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(75, 60)]
    [InlineData(120, 120)]
    [InlineData(144, 144)]
    public void AutoTarget_FollowsRefreshRate(int hz, int expected) => Assert.Equal(expected, GameOptimizer.AutoTargetFps(hz));

    [Fact]
    public void Recommendation_AlwaysHasCoreSettings()
    {
        var r = GameOptimizer.Recommend(Game(GameClass.Medium), Hw(), Scores(16958));
        foreach (var name in new[] { "Разрешение", "Общий пресет", "Текстуры", "Тени" })
            Assert.Contains(r.Settings, s => s.Name == name);
    }
}

public class BenchmarkTests
{
    private const string GpuCsv = "gpuName,G3Dmark,G2Dmark\nGeForce RTX 3060,16958,951\n\"Radeon, Test\",5000,1\nGeForce RTX 3060 Ti,20206,961\n";

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060", "geforce rtx 3060")]
    [InlineData("AMD Radeon RX 6600 XT", "radeon rx 6600 xt")]
    public void NormalizeGpu(string name, string expected) => Assert.Equal(expected, BenchmarkService.NormalizeGpu(name));

    [Theory]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12400F", "core i5-12400f")]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", "ryzen 5 5600x")]
    [InlineData("AMD Ryzen 5 5600G with Radeon Graphics", "ryzen 5 5600g")]
    public void NormalizeCpu(string name, string expected) => Assert.Equal(expected, BenchmarkService.NormalizeCpu(name));

    [Fact]
    public void Table_ParsesQuotedFields_AndFindsExactAndPartial()
    {
        var table = BenchmarkService.ParseTable(GpuCsv, "gpuName", "G3Dmark", BenchmarkService.NormalizeGpu);
        Assert.Equal(3, table.Count);
        Assert.Equal(16958, BenchmarkService.Find(table, "geforce rtx 3060"));
        Assert.Equal(20206, BenchmarkService.Find(table, "geforce rtx 3060 ti"));
        // «Laptop»-вариант не найден точно: берётся самая длинная подходящая запись, а не Ti
        Assert.Equal(16958, BenchmarkService.Find(table, "geforce rtx 3060 super duper"));
        Assert.Null(BenchmarkService.Find(table, "totally unknown"));
    }

    [Fact]
    public void Resolve_UsesEstimateForNewGpu_AndThreadsFallbackForCpu()
    {
        var hw = new HardwareProfile("Unknown CPU 9000", 8, 16, 5.0, 32, "NVIDIA GeForce RTX 4070", 12, 2560, 1440, 165);
        var s = BenchmarkService.Resolve(hw, new(), new());
        Assert.Equal(27000, s.Gpu);
        Assert.Contains("оценка", s.GpuSource);
        Assert.Equal(16 * 5.0 * 360, s.Cpu);
    }

    [Fact]
    public void Estimate_LongerNamesWinOverShorter()
    {
        Assert.Equal(31500, BenchmarkService.EstimateGpu("GeForce RTX 4070 Ti"));
        Assert.Equal(34000, BenchmarkService.EstimateGpu("GeForce RTX 4070 Ti SUPER"));
    }
}

public class GameKnowledgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-game-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* не критично */ }
    }

    [Theory]
    [InlineData("Cyberpunk 2077", GameClass.Heavy)]
    [InlineData("Counter-Strike 2", GameClass.Light)]
    [InlineData("Alan Wake 2", GameClass.VeryHeavy)]
    [InlineData("Some Unknown Indie", GameClass.Medium)]
    public void Classify_ByName(string name, GameClass expected) => Assert.Equal(expected, GameKnowledge.Classify(name).Class);

    [Fact]
    public void Analyze_FindsUpscalerLibraries()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "nvngx_dlss.dll"), "x");
        File.WriteAllText(Path.Combine(_dir, "libxess.dll"), "x");
        File.WriteAllText(Path.Combine(_dir, "UnityPlayer.dll"), "x");

        var t = GameKnowledge.Analyze("Test Game", _dir);
        Assert.True(t.Dlss);
        Assert.True(t.Xess);
        Assert.False(t.Fsr);
        Assert.Equal("Unity", t.Engine);
    }

    [Fact]
    public void Analyze_MissingFolder_DoesNotThrow() =>
        Assert.Null(Record.Exception(() => GameKnowledge.Analyze("X", Path.Combine(_dir, "nope"))));
}

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* не критично */ }
    }

    [Fact]
    public void Defaults_WhenFileMissing()
    {
        var s = WinOptimizer.Infrastructure.AppSettings.Load(Path.Combine(_dir, "none.json"));
        Assert.True(s.ShowWelcome);
        Assert.True(s.CheckUpdatesOnStartup);
        Assert.True(s.Animations);
        Assert.Equal(100, s.UiScalePercent);
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var path = Path.Combine(_dir, "s.json");
        var s = new WinOptimizer.Infrastructure.AppSettings { ShowWelcome = false, Animations = false, UiScalePercent = 125 };
        s.Save(path);
        var back = WinOptimizer.Infrastructure.AppSettings.Load(path);
        Assert.False(back.ShowWelcome);
        Assert.False(back.Animations);
        Assert.Equal(125, back.UiScalePercent);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ не json");
        Assert.True(WinOptimizer.Infrastructure.AppSettings.Load(path).ShowWelcome);
    }

    [Fact]
    public void InvalidScale_IsNormalized()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "scale.json");
        File.WriteAllText(path, "{\"UiScalePercent\": 500}");
        Assert.Equal(100, WinOptimizer.Infrastructure.AppSettings.Load(path).UiScalePercent);
    }

    [Theory]
    [InlineData(512, "512 Б")]
    [InlineData(2048, "2 КБ")]
    [InlineData(5 * 1024 * 1024, "5 МБ")]
    public void FormatSize(long bytes, string expected) =>
        Assert.Equal(expected, WinOptimizer.ViewModels.SettingsViewModel.FormatSize(bytes));
}

public class AutostartTests
{
    [Fact]
    public void CreateArguments_QuoteThePathAndRunElevated()
    {
        var args = WinOptimizer.Services.AutostartService.BuildCreateArguments(@"C:\Program Files\WinOptimizer\WinOptimizer.exe");
        Assert.Contains("/rl highest", args);
        Assert.Contains("/sc onlogon", args);
        Assert.Contains("\\\"C:\\Program Files\\WinOptimizer\\WinOptimizer.exe\\\"", args);
    }

    [Fact]
    public void RestorePointPrompt_IsOnByDefault_AndResets()
    {
        var s = new WinOptimizer.Infrastructure.AppSettings { AskRestorePoint = false };
        s.ResetToDefaults();
        Assert.True(s.AskRestorePoint);
    }
}

public class TargetFpsTests
{
    private static readonly WinOptimizer.Services.HardwareProfile Hw =
        new("CPU", 8, 16, 4.5, 16, "NVIDIA GeForce RTX 3060", 12, 1920, 1080, 144);
    private static readonly WinOptimizer.Services.HardwareScores Sc = new(17000, "test", 20000, "test");
    private static readonly WinOptimizer.Services.GameTraits Game =
        new("Game", WinOptimizer.Services.GameClass.Heavy, "test", true, false, true, false, false, null, null);

    [Fact]
    public void ManualTarget_OverridesAuto()
    {
        var auto = WinOptimizer.Services.GameOptimizer.Recommend(Game, Hw, Sc);
        var manual = WinOptimizer.Services.GameOptimizer.Recommend(Game, Hw, Sc, 60);
        Assert.Equal(144, auto.TargetFps);
        Assert.Equal(60, manual.TargetFps);
    }

    [Fact]
    public void ListValuesAreSortedAndStartWithAuto()
    {
        var v = WinOptimizer.ViewModels.GamesViewModel.TargetFpsValues;
        Assert.Equal(0, v[0]);
        Assert.Equal(v.Skip(1).OrderBy(x => x), v.Skip(1));
    }
}

public class UpdateChannelTests
{
    private const string Json = """
        [{"tag_name":"v0.4.60-beta","prerelease":true,"draft":false},
         {"tag_name":"v0.4.59","prerelease":false,"draft":true},
         {"tag_name":"v0.4.58","prerelease":false,"draft":false}]
        """;

    private static string? Tag(bool beta)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Json);
        return UpdateService.PickRelease(doc.RootElement, beta)?.GetProperty("tag_name").GetString();
    }

    [Fact] public void Stable_SkipsBetaAndDrafts() => Assert.Equal("v0.4.58", Tag(false));
    [Fact] public void Beta_TakesNewestPublished() => Assert.Equal("v0.4.60-beta", Tag(true));

    [Fact]
    public void PickRelease_EmptyOrNotArray_ReturnsNull()
    {
        using var empty = System.Text.Json.JsonDocument.Parse("[]");
        using var obj = System.Text.Json.JsonDocument.Parse("{}");
        Assert.Null(UpdateService.PickRelease(empty.RootElement, true));
        Assert.Null(UpdateService.PickRelease(obj.RootElement, true));
    }

    [Theory]
    [InlineData("0.4.50", "stable", "0.4.60", "beta", true, true)]
    [InlineData("0.4.50", "stable", "0.4.50", "stable", false, false)]
    [InlineData("0.4.60", "beta", "0.4.58", "stable", false, true)]   // возврат с беты на стабильную
    [InlineData("0.4.60", "beta", "0.4.58", "stable", true, false)]   // остаёмся на бете
    [InlineData("0.4.60", "beta", "0.4.60", "stable", false, false)]
    public void IsUpdate_Rules(string cur, string curCh, string latest, string latestCh, bool beta, bool expected) =>
        Assert.Equal(expected, UpdateService.IsUpdate(cur, curCh, latest, latestCh, beta));

    [Theory]
    [InlineData("0.4.60-beta", "beta")]
    [InlineData("0.4.60-stable", "stable")]
    [InlineData("0.4.60-dev", "stable")]
    public void ChannelOfLabel(string label, string expected) => Assert.Equal(expected, UpdateService.ChannelOfLabel(label));

    [Fact]
    public void UnknownChannel_IsNormalizedToStable()
    {
        var s = new WinOptimizer.Infrastructure.AppSettings { UpdateChannel = "nightly" };
        s.Normalize();
        Assert.Equal("stable", s.UpdateChannel);
    }
}

public class TabVisibilityAndStoreTests
{
    [Fact]
    public void HidingAndRestoringTab()
    {
        var s = new WinOptimizer.Infrastructure.AppSettings();
        Assert.True(s.IsTabVisible("Store"));
        s.SetTabVisible("Store", false);
        Assert.False(s.IsTabVisible("Store"));
        s.SetTabVisible("Store", false);
        Assert.Single(s.HiddenTabs);
        s.SetTabVisible("Store", true);
        Assert.True(s.IsTabVisible("Store"));
    }

    [Fact]
    public void UnknownOrSettingsTab_CannotBeHidden()
    {
        var s = new WinOptimizer.Infrastructure.AppSettings();
        s.SetTabVisible("Settings", false);
        s.SetTabVisible("Nope", false);
        Assert.Empty(s.HiddenTabs);
    }

    [Fact]
    public void Normalize_DropsUnknownAndDuplicates_AndRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "wo-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new WinOptimizer.Infrastructure.AppSettings { HiddenTabs = new() { "Store", "Store", "Bogus", "Settings", "Games" } };
            s.Save(path);
            var loaded = WinOptimizer.Infrastructure.AppSettings.Load(path);
            Assert.Equal(new[] { "Store", "Games" }, loaded.HiddenTabs);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reset_ShowsAllTabs()
    {
        var s = new WinOptimizer.Infrastructure.AppSettings();
        s.SetTabVisible("Games", false);
        s.ResetToDefaults();
        Assert.Empty(s.HiddenTabs);
    }

    [Fact]
    public void SpotifyHasWingetFallback()
    {
        var spotify = new StoreAppInfo("9NCBCSZSJRSB", "Spotify", "Spotify AB", "msstore", ProductId: "9NCBCSZSJRSB");
        Assert.Equal("Spotify.Spotify", MsStoreService.AlternativeWingetId(spotify));
        Assert.Null(MsStoreService.AlternativeWingetId(new StoreAppInfo("X", "Unknown", "p", "msstore")));
    }
}

public class InstallerAndPersistenceTests
{
    private static readonly (string Name, string Url)[] Assets =
    {
        ("WinOptimizer-win-x64.zip", "zip-url"), ("WinOptimizer-Setup.exe", "setup-url"),
    };

    [Fact] public void InstalledCopy_GetsInstaller() => Assert.Equal("setup-url", UpdateService.PickAsset(Assets, true));
    [Fact] public void PortableCopy_GetsZip() => Assert.Equal("zip-url", UpdateService.PickAsset(Assets, false));
    [Fact] public void OnlyZip_UsedEvenForInstalledCopy() => Assert.Equal("zip-url", UpdateService.PickAsset(new[] { Assets[0] }, true));
    [Fact] public void NoAssets_ReturnsNull() => Assert.Null(UpdateService.PickAsset(Array.Empty<(string, string)>(), true));

    [Fact]
    public void TargetFpsAndTabs_SurviveRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "wo-persist-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new WinOptimizer.Infrastructure.AppSettings { GamesTargetFps = 120 };
            s.SetTabVisible("Store", false);
            s.Save(path);
            var loaded = WinOptimizer.Infrastructure.AppSettings.Load(path);
            Assert.Equal(120, loaded.GamesTargetFps);
            Assert.False(loaded.IsTabVisible("Store"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InvalidTargetFps_FallsBackToAuto()
    {
        var s = new WinOptimizer.Infrastructure.AppSettings { GamesTargetFps = 77 };
        s.Normalize();
        Assert.Equal(0, s.GamesTargetFps);
    }
}

public class AssetScoreTests
{
    [Fact]
    public void LargerAssetsWin()
    {
        var names = new[] { "Logo.targetsize-16.png", "Logo.scale-100.png", "Logo.scale-200.png", "Logo.targetsize-256.png", "Logo.targetsize-256_altform-unplated.png" };
        var best = names.OrderByDescending(DebloatService.AssetScore).First();
        Assert.Equal("Logo.targetsize-256.png", best);
        Assert.True(DebloatService.AssetScore("Logo.scale-200.png") > DebloatService.AssetScore("Logo.scale-100.png"));
        Assert.True(DebloatService.AssetScore("Logo.scale-100.png") > DebloatService.AssetScore("Logo.targetsize-16.png"));
    }
}

public class ThemeTests
{
    [Fact]
    public void Themes_HaveUniqueIds_AndDefaultExists()
    {
        var ids = WinOptimizer.Infrastructure.ThemeManager.Themes.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains(WinOptimizer.Infrastructure.ThemeManager.DefaultTheme, ids);
        Assert.True(ids.Count >= 6);
    }

    [Fact]
    public void EveryTheme_TextIsReadableOnCards()
    {
        foreach (var t in WinOptimizer.Infrastructure.ThemeManager.Themes)
        {
            var p = t.Palette;
            Assert.True(WinOptimizer.Infrastructure.ThemeManager.Contrast(p.TextPrimary, p.BgCard) >= 7, t.Id + " primary");
            Assert.True(WinOptimizer.Infrastructure.ThemeManager.Contrast(p.TextSecondary, p.BgCard) >= 4.5, t.Id + " secondary");
            Assert.True(WinOptimizer.Infrastructure.ThemeManager.Contrast(p.TextDim, p.BgCard) >= 4.5, t.Id + " dim");
            Assert.True(WinOptimizer.Infrastructure.ThemeManager.Contrast(p.TextPrimary, p.BgWindow) >= 7, t.Id + " on window");
            Assert.True(WinOptimizer.Infrastructure.ThemeManager.Contrast(p.Accent, p.BgCard) >= 3, t.Id + " accent");
        }
    }

    [Fact]
    public void Settings_NormalizeClampsAppearanceValues()
    {
        var s = new WinOptimizer.Infrastructure.AppSettings
        {
            ThemeId = "nope", AccentId = "nope", BackgroundBlurPercent = 500, BackgroundDimPercent = -5,
            CardOpacityPercent = 1, BackgroundImage = @"C:\definitely\missing.png",
        };
        s.Normalize();
        Assert.Equal("dark", s.ThemeId);
        Assert.Equal("theme", s.AccentId);
        Assert.Equal(100, s.BackgroundBlurPercent);
        Assert.Equal(0, s.BackgroundDimPercent);
        Assert.Equal(50, s.CardOpacityPercent);
        Assert.Equal("", s.BackgroundImage);
    }

    [Fact]
    public void Settings_ThemeAndAccentRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "wo-theme-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new WinOptimizer.Infrastructure.AppSettings { ThemeId = "redblack", AccentId = "green" }.Save(path);
            var loaded = WinOptimizer.Infrastructure.AppSettings.Load(path);
            Assert.Equal("redblack", loaded.ThemeId);
            Assert.Equal("green", loaded.AccentId);
        }
        finally { File.Delete(path); }
    }
}

public class PageFileTweakTests
{
    [Theory]
    [InlineData(2L * 1024 * 1024 * 1024, 4096)]    // мало ОЗУ — нижняя граница
    [InlineData(8L * 1024 * 1024 * 1024, 4096)]
    [InlineData(12L * 1024 * 1024 * 1024, 6144)]
    [InlineData(16L * 1024 * 1024 * 1024, 8192)]
    [InlineData(64L * 1024 * 1024 * 1024, 8192)]   // много ОЗУ — верхняя граница
    [InlineData(-1L, 4096)]
    public void RecommendedSize_IsClampedAndAligned(long ramBytes, int expectedMb)
    {
        var size = PageFileTweak.RecommendedSizeMb(ramBytes);
        Assert.Equal(expectedMb, size);
        Assert.Equal(0, size % 256);
    }

    [Theory]
    [InlineData("C:", 4096, @"C:\pagefile.sys 4096 4096")]
    [InlineData(@"d:\", 8192, @"D:\pagefile.sys 8192 8192")]
    public void BuildEntry_FormatsPagingFilesLine(string drive, int mb, string expected) =>
        Assert.Equal(expected, PageFileTweak.BuildEntry(drive, mb));

    [Theory]
    [InlineData("")]
    [InlineData("CD:")]
    [InlineData("1:")]
    public void BuildEntry_RejectsBadDrive(string drive) =>
        Assert.Throws<ArgumentException>(() => PageFileTweak.BuildEntry(drive, 4096));
}

public class NewsInterestsTweakTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* не критично */ }
    }

    [Fact]
    public void Catalog_HasPageFileAndNewsTweaks()
    {
        var ids = TweakCatalog.Create(new BackupStore(Path.Combine(_dir, "b.json"))).Select(t => t.Info.Id).ToList();
        Assert.Contains("pagefile-fixed", ids);
        Assert.Contains("news-interests-off", ids);
    }
}

public class TemperatureReaderTests
{
    [Theory]
    [InlineData(3231, 49.95)]    // 323.1 K
    [InlineData(3000, 26.85)]
    public void FromDeciKelvin_ConvertsToCelsius(int tenths, double expected) =>
        Assert.Equal(expected, TemperatureReader.FromDeciKelvin(tenths)!.Value, 2);

    [Theory]
    [InlineData(0)]            // 0 К — датчика нет
    [InlineData(99999)]        // мусор
    public void FromDeciKelvin_RejectsImplausible(int tenths) =>
        Assert.Null(TemperatureReader.FromDeciKelvin(tenths));

    [Fact]
    public void FromKelvin_ConvertsAndHandlesNull()
    {
        Assert.Equal(26.85, TemperatureReader.FromKelvin(300u)!.Value, 2);
        Assert.Null(TemperatureReader.FromKelvin(null));
    }

    [Fact]
    public void ParseNvidiaSmi_ReadsRowsAndSkipsGarbage()
    {
        var rows = TemperatureReader.ParseNvidiaSmi("NVIDIA GeForce RTX 4060, 55\r\nbroken line\r\nTesla T4, [N/A]\r\nRTX A2000, 41\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(("NVIDIA GeForce RTX 4060", 55d), rows[0]);
        Assert.Equal(41d, rows[1].Celsius);
    }

    [Fact]
    public void NvidiaInfoLines_PrefixesModelWithGpuLabel()
    {
        var lines = TemperatureReader.NvidiaInfoLines("NVIDIA GeForce RTX 4060, 55");

        var line = Assert.Single(lines);
        Assert.Equal("GPU · NVIDIA GeForce RTX 4060", line.Label);
        Assert.Equal("55 °C", line.Value);
    }

    [Theory]
    [InlineData(@"\_TZ.TZ00", "TZ00")]
    [InlineData("THRM", "THRM")]
    public void ZoneName_IsShortened(string raw, string expected) =>
        Assert.Equal(expected, TemperatureReader.ZoneName(raw));
}

public class MasonryLayoutTests
{
    [Theory]
    [InlineData(0, 340, 1)]
    [InlineData(300, 340, 1)]
    [InlineData(700, 340, 2)]
    [InlineData(1110, 340, 3)]
    [InlineData(1110, 0, 1)]
    public void ColumnCount_FitsWidth(double width, double min, int expected) =>
        Assert.Equal(expected, WinOptimizer.Views.MasonryPanel.ColumnCount(width, min));

    [Fact]
    public void ShortestColumn_PicksFirstMinimum()
    {
        Assert.Equal(1, WinOptimizer.Views.MasonryPanel.ShortestColumn([120, 80, 80]));
        Assert.Equal(0, WinOptimizer.Views.MasonryPanel.ShortestColumn([0, 0]));
    }
}

public class CardEntranceTests
{
    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 45)]
    [InlineData(12, 540)]
    [InlineData(500, 540)]   // каскад ограничен: длинный список не «доезжает» секундами
    public void CascadeDelay_IsClamped(int index, int expectedMs) =>
        Assert.Equal(expectedMs, WinOptimizer.Views.Motion.CascadeDelayMs(index));
}

public class PowerPlanNamesTests
{
    [Theory]
    [InlineData("GUID схемы питания: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)", "Сбалансированная")]
    [InlineData("Power Scheme GUID: 8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C  (High performance)", "Высокая производительность")]
    [InlineData("GUID схемы питания: a1841308-3541-4fab-bc81-f71556f20b4a  (Power saver)", "Экономия энергии")]
    [InlineData("GUID схемы питания: e9a42b02-d5df-448d-aa00-03f14749eb61  (Ultimate Performance)", "Максимальная производительность")]
    public void StockPlans_GetRussianNames(string output, string expected) =>
        Assert.Equal(expected, PowerPlanNames.Describe(output));

    [Fact]
    public void CustomPlan_KeepsSystemName() =>
        Assert.Equal("SKID AMD", PowerPlanNames.Describe("GUID схемы питания: 11111111-2222-3333-4444-555555555555  (SKID AMD)"));

    [Fact]
    public void NoName_FallsBackToGuid() =>
        Assert.Equal("11111111-2222-3333-4444-555555555555", PowerPlanNames.Describe("GUID: 11111111-2222-3333-4444-555555555555"));

    [Fact]
    public void ReadActive_NeverThrows_AndIsReadable()
    {
        var name = PowerPlanNames.ReadActive();
        // На Windows имя есть и не содержит символа замены «�» (признак битой кодировки); на других ОС — null.
        if (name is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.DoesNotContain('\uFFFD', name);
        }
    }

    [Fact]
    public void OemEncoding_IsAvailable() => Assert.NotNull(WinOptimizer.Services.ProcessRunner.OemEncoding);
}


public class ReleaseNotesFormatterTests
{
    [Fact]
    public void Summarize_StripsCommitPrefixTrailersAndPullRefs()
    {
        var text = "fix(ui): OneWay binding for progress bars (#7)\n\nCo-Authored-By: Claude <noreply@anthropic.com>\nClaude-Session: https://claude.ai/x";
        Assert.Equal("• Исправлено: OneWay binding for progress bars", ReleaseNotesFormatter.Summarize(text));
    }

    [Fact]
    public void Summarize_LimitsItemsAndMarksTheRest()
    {
        var text = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"- Пункт {i}"));
        var result = ReleaseNotesFormatter.Summarize(text, maxItems: 3);
        Assert.Equal("• Пункт 1\n• Пункт 2\n• Пункт 3\n• …и другие изменения", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("## Что нового\nFull Changelog: https://github.com/a/b")]
    public void Summarize_ReturnsEmptyWhenNothingUseful(string? text) =>
        Assert.Equal(string.Empty, ReleaseNotesFormatter.Summarize(text));

    [Fact]
    public void Summarize_TruncatesLongLinesAndDeduplicates()
    {
        var long_ = new string('а', 300);
        var result = ReleaseNotesFormatter.Summarize($"{long_}\nОдно и то же\nодно и то же");
        var lines = result.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("…", lines[0]);
    }
}
