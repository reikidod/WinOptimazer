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
