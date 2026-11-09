using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class OptimizationProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "winoptimizer-profile-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsSelectedTweaks()
    {
        var path = Path.Combine(_directory, "profile.json");
        var profile = OptimizationProfileService.Create("Игровой профиль", ["gpu.high", "network.latency", "gpu.high"]);

        OptimizationProfileService.Save(path, profile);
        var loaded = OptimizationProfileService.Load(path);

        Assert.Equal("Игровой профиль", loaded.Name);
        Assert.Equal(new[] { "gpu.high", "network.latency" }, loaded.SelectedTweakIds);
    }

    [Fact]
    public void Load_RejectsUnsupportedSchema()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "future.json");
        File.WriteAllText(path, """{"SchemaVersion":99,"Name":"Future","CreatedAt":"2026-01-01T00:00:00+00:00","SelectedTweakIds":[]}""");

        Assert.Throws<InvalidDataException>(() => OptimizationProfileService.Load(path));
    }
}
