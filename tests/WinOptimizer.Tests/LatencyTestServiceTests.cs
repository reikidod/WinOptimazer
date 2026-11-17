using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class LatencyTestServiceTests
{
    [Fact]
    public void Analyze_ReturnsStableLatencyStatistics()
    {
        var result = LatencyTestService.Analyze(new[] { 10d, 20d, 30d, 40d, 100d });

        Assert.Equal(40, result.AverageMicroseconds);
        Assert.Equal(30, result.MedianMicroseconds);
        Assert.Equal(100, result.P95Microseconds);
        Assert.Equal(100, result.MaximumMicroseconds);
        Assert.Equal(5, result.SampleCount);
    }

    [Fact]
    public void Analyze_RejectsEmptyOrInvalidSamples()
    {
        Assert.Throws<ArgumentException>(() => LatencyTestService.Analyze(Array.Empty<double>()));
        Assert.Throws<ArgumentException>(() => LatencyTestService.Analyze(new[] { 1d, double.NaN }));
        Assert.Throws<ArgumentException>(() => LatencyTestService.Analyze(new[] { -1d }));
    }
}
