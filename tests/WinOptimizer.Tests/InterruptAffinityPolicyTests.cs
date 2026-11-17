using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class InterruptAffinityPolicyTests
{
    [Theory]
    [InlineData(1, 2, 0x02)]
    [InlineData(7, 8, 0x80)]
    [InlineData(8, 9, 0x01)]
    [InlineData(63, 64, 0x80)]
    [InlineData(0, 1, 0x01)]
    public void CreateProcessorMask_SetsOnlyRequestedProcessor(int processor, int count, byte expectedByte)
    {
        var mask = InterruptAffinityService.CreateProcessorMask(processor, count);

        Assert.Equal(8, mask.Length);
        Assert.Equal(expectedByte, mask[processor / 8]);
        Assert.Equal(1, mask.Sum(value => System.Numerics.BitOperations.PopCount((uint)value)));
    }

    [Fact]
    public void CreateProcessorMask_SupportsSeveralLogicalProcessorsAcrossPhysicalCores()
    {
        var mask = InterruptAffinityService.CreateProcessorMask(new[] { 0, 4, 8 }, 12);

        Assert.Equal(8, mask.Length);
        Assert.Equal(0x11, mask[0]);
        Assert.Equal(0x01, mask[1]);
        Assert.Equal(3, mask.Sum(value => System.Numerics.BitOperations.PopCount((uint)value)));
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(8, 8)]
    [InlineData(-1, 8)]
    [InlineData(1, 0)]
    [InlineData(1, 65)]
    public void CreateProcessorMask_RejectsOutOfRangeTopology(int processor, int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InterruptAffinityService.CreateProcessorMask(processor, count));
    }

    [Fact]
    public void CreateProcessorMask_RejectsEmptySelection()
    {
        Assert.Throws<ArgumentException>(() =>
            InterruptAffinityService.CreateProcessorMask(Array.Empty<int>(), 12));
    }
}
