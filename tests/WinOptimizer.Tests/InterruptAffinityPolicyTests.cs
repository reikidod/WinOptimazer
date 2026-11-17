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

    [Fact]
    public void CreateCoreProcessorMask_IncludesAllSmtThreadsForSelectedPhysicalCores()
    {
        var cores = new[]
        {
            new InterruptAffinityCore(0, new[] { 0, 6 }),
            new InterruptAffinityCore(1, new[] { 1, 7 }),
            new InterruptAffinityCore(2, new[] { 2, 8 })
        };

        var mask = InterruptAffinityService.CreateCoreProcessorMask(cores, new[] { 0, 2 }, 12);

        Assert.Equal((1UL << 0) | (1UL << 6) | (1UL << 2) | (1UL << 8), mask);
    }

    [Fact]
    public void CreateCoreProcessorMask_RejectsEmptyPhysicalCoreSelection()
    {
        var cores = new[] { new InterruptAffinityCore(0, new[] { 0, 6 }) };

        Assert.Throws<ArgumentException>(() =>
            InterruptAffinityService.CreateCoreProcessorMask(cores, Array.Empty<int>(), 12));
    }
}
