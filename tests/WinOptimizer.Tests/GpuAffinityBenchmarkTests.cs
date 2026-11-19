using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class GpuAffinityBenchmarkTests
{
    [Fact]
    public void ParseLiblavaBenchmarkJson_CalculatesAverageAndOnePercentLow()
    {
        var frames = Enumerable.Repeat(10.0, 990).Concat(Enumerable.Repeat(20.0, 10));
        var json = System.Text.Json.JsonSerializer.Serialize(new { frames });

        var result = LiblavaBenchmarkParser.Parse(json);

        Assert.Equal(1000, result.FrameCount);
        Assert.Equal(99.0099, result.AverageFps, 3);
        Assert.Equal(50.0, result.OnePercentLowFps, 3);
        Assert.Equal(10.0, result.P99FrameTimeMs, 3);
    }

    [Fact]
    public void ParseLiblavaBenchmarkJson_IgnoresInvalidFrameValues()
    {
        var expanded = System.Text.Json.JsonSerializer.Serialize(new
        {
            frames = Enumerable.Repeat(8.0, 100).Concat(new[] { 0.0, -1.0 })
        });

        var result = GpuAffinityBenchmark.ParseLiblavaBenchmarkJson(expanded);

        Assert.Equal(100, result.FrameCount);
        Assert.Equal(125.0, result.AverageFps, 3);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData(@"{""frames"":[]}")]
    [InlineData(@"{""frames"":[8,8,8]}")]
    public void ParseLiblavaBenchmarkJson_RejectsMissingOrInsufficientFrames(string json)
    {
        Assert.Throws<InvalidDataException>(() => GpuAffinityBenchmark.ParseLiblavaBenchmarkJson(json));
    }
}
