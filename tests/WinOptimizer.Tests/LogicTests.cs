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
