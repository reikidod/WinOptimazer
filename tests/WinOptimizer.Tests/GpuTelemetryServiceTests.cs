using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class GpuTelemetryServiceTests
{
    [Fact]
    public void AggregateUtilization_SumsProcessesPerPhysicalEngine_AndTakesMaximumEnginePerAdapter()
    {
        var samples = new[]
        {
            new GpuEngineUtilizationSample(0, 0, "3D", 20),
            new GpuEngineUtilizationSample(0, 0, "3D", 35),
            new GpuEngineUtilizationSample(0, 1, "Copy", 70),
            new GpuEngineUtilizationSample(1, 0, "3D", 15),
        };

        var result = GpuTelemetryService.AggregateUtilization(samples);

        Assert.Equal(70d, result[0]);
        Assert.Equal(15d, result[1]);
    }

    [Fact]
    public void AggregateUtilization_ClampsEngineTotalsAndIgnoresInvalidSamples()
    {
        var samples = new[]
        {
            new GpuEngineUtilizationSample(0, 0, "3D", 80),
            new GpuEngineUtilizationSample(0, 0, "3D", 45),
            new GpuEngineUtilizationSample(0, 1, "Copy", double.NaN),
            new GpuEngineUtilizationSample(-1, 0, "3D", 90),
            new GpuEngineUtilizationSample(0, -1, "3D", 90),
            new GpuEngineUtilizationSample(0, 2, " ", 90),
            new GpuEngineUtilizationSample(1, 0, "3D", -1),
        };

        var result = GpuTelemetryService.AggregateUtilization(samples);

        Assert.Equal(100d, result[0]);
        Assert.DoesNotContain(1, result.Keys);
    }

    [Fact]
    public void TryGetEngineIndex_ParsesWindowsGpuEngineInstance()
    {
        const string instance = "pid_1234_luid_0x00000000_0x00001234_phys_0_eng_7_engtype_3D";

        Assert.True(GpuTelemetryService.TryGetEngineIndex(instance, out var engineIndex));
        Assert.Equal(7, engineIndex);
        Assert.True(GpuTelemetryService.TryGetEngineType(instance, out var engineType));
        Assert.Equal("3D", engineType);
        Assert.True(GpuTelemetryService.TryGetAdapterIndex(instance, out var adapterIndex));
        Assert.Equal(0, adapterIndex);
    }
}
