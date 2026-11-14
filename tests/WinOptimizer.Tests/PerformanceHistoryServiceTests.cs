using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class PerformanceHistoryServiceTests
{
    private static SystemSnapshot CreateSnapshot(
        DateTime capturedAt,
        bool cpuAvailable = true,
        IReadOnlyList<GpuAdapterActivitySnapshot>? gpu = null,
        IReadOnlyList<PhysicalDiskActivitySnapshot>? disks = null,
        NetworkTrafficRate? network = null,
        uint memoryLoad = 50,
        double cpuPercent = 37.5)
    {
        return new SystemSnapshot(
            "Windows 11",
            8,
            cpuPercent,
            memoryLoad,
            16UL * 1024 * 1024 * 1024,
            8UL * 1024 * 1024 * 1024,
            "C:\\",
            500L * 1024 * 1024 * 1024,
            250L * 1024 * 1024 * 1024,
            true)
        {
            CpuAvailable = cpuAvailable,
            GpuActivity = gpu ?? Array.Empty<GpuAdapterActivitySnapshot>(),
            PhysicalDiskActivity = disks ?? Array.Empty<PhysicalDiskActivitySnapshot>(),
            NetworkTraffic = network ?? new NetworkTrafficRate(null, null),
            CapturedAt = capturedAt,
        };
    }

    [Fact]
    public void Add_CapturesGpuPeakDiskTotalsAndNetworkRates()
    {
        var capturedAt = new DateTime(2026, 11, 14, 10, 30, 0, DateTimeKind.Utc);
        var snapshot = CreateSnapshot(
            capturedAt,
            gpu:
            [
                new GpuAdapterActivitySnapshot(0, 35.5, capturedAt),
                new GpuAdapterActivitySnapshot(1, 75.5, capturedAt),
            ],
            disks:
            [
                new PhysicalDiskActivitySnapshot("0 C:", 1_000, 2_000, capturedAt),
                new PhysicalDiskActivitySnapshot("1 D:", 4_000, 8_000, capturedAt),
            ],
            network: new NetworkTrafficRate(2_048, 1_024));

        var point = new PerformanceHistoryService().Add(snapshot);

        Assert.Equal(capturedAt.ToLocalTime(), point.CapturedAt);
        Assert.Equal(75.5, point.GpuPercent);
        Assert.Equal(5_000d, point.DiskReadBytesPerSecond!.Value);
        Assert.Equal(10_000d, point.DiskWriteBytesPerSecond!.Value);
        Assert.Equal(2_048d, point.NetworkDownloadBytesPerSecond!.Value);
        Assert.Equal(1_024d, point.NetworkUploadBytesPerSecond!.Value);
        Assert.True(point.GpuBarHeight > 2d);
        Assert.True(point.DiskBarHeight > 2d);
        Assert.True(point.NetworkBarHeight > 2d);
        Assert.Contains("GPU пик 75.5%", point.TooltipText);
        Assert.Contains("Диск ↓", point.TooltipText);
        Assert.Contains("Сеть ↓", point.TooltipText);
    }

    [Fact]
    public void Add_KeepsWarmingAndUnavailableMetricsUnknown()
    {
        var capturedAt = new DateTime(2026, 11, 14, 10, 30, 0, DateTimeKind.Utc);
        var snapshot = CreateSnapshot(
            capturedAt,
            cpuAvailable: false,
            gpu: [new GpuAdapterActivitySnapshot(0, null, capturedAt, IsWarmingUp: true)],
            disks: [new PhysicalDiskActivitySnapshot("0 C:", null, null, capturedAt) { IsWarmingUp = true }],
            network: new NetworkTrafficRate(null, null));

        var point = new PerformanceHistoryService().Add(snapshot);

        Assert.Null(point.CpuPercent);
        Assert.Null(point.GpuPercent);
        Assert.Null(point.DiskReadBytesPerSecond);
        Assert.Null(point.DiskWriteBytesPerSecond);
        Assert.Null(point.NetworkDownloadBytesPerSecond);
        Assert.Null(point.NetworkUploadBytesPerSecond);
        Assert.Equal(2d, point.CpuBarHeight);
        Assert.Equal(2d, point.GpuBarHeight);
        Assert.Equal(2d, point.DiskBarHeight);
        Assert.Equal(2d, point.NetworkBarHeight);
        Assert.Equal(0.22d, point.GpuBarOpacity);
        Assert.Equal(0.22d, point.DiskBarOpacity);
        Assert.Equal(0.22d, point.NetworkBarOpacity);
    }

    [Fact]
    public void Add_DoesNotReportPartialDiskTotalsAsComplete()
    {
        var capturedAt = new DateTime(2026, 11, 14, 10, 30, 0, DateTimeKind.Utc);
        var snapshot = CreateSnapshot(
            capturedAt,
            disks:
            [
                new PhysicalDiskActivitySnapshot("0 C:", 1_000, 2_000, capturedAt),
                new PhysicalDiskActivitySnapshot("1 D:", null, null, capturedAt),
            ]);

        var point = new PerformanceHistoryService().Add(snapshot);

        Assert.Null(point.DiskReadBytesPerSecond);
        Assert.Null(point.DiskWriteBytesPerSecond);
        Assert.Equal(2d, point.DiskBarHeight);
    }

    [Fact]
    public void Add_EvictsOldestSamplesWhenCapacityIsReached()
    {
        var service = new PerformanceHistoryService(capacity: 2);
        var first = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        service.Add(CreateSnapshot(first));
        service.Add(CreateSnapshot(first.AddSeconds(2)));
        service.Add(CreateSnapshot(first.AddSeconds(4)));

        Assert.Equal(2, service.Samples.Count);
        Assert.Equal(first.AddSeconds(2).ToLocalTime(), service.Samples[0].CapturedAt);
        Assert.Equal(first.AddSeconds(4).ToLocalTime(), service.Samples[1].CapturedAt);
    }

    [Fact]
    public void Add_RejectsInvalidCpuAndGpuPercentages()
    {
        var capturedAt = new DateTime(2026, 11, 14, 10, 30, 0, DateTimeKind.Utc);
        var snapshot = CreateSnapshot(
            capturedAt,
            cpuPercent: double.NaN,
            gpu:
            [
                new GpuAdapterActivitySnapshot(0, double.NaN, capturedAt),
                new GpuAdapterActivitySnapshot(1, -5, capturedAt),
            ]);

        var point = new PerformanceHistoryService().Add(snapshot);

        Assert.Null(point.CpuPercent);
        Assert.Null(point.GpuPercent);
        Assert.Equal("CPU недоступен", point.CpuValueText);
        Assert.Equal("GPU н/д", point.GpuValueText);
    }

    [Fact]
    public void Add_KeepsHistoryBoundedDuringLongSessions()
    {
        const int capacity = 60;
        const int sampleCount = 10_000;
        var service = new PerformanceHistoryService(capacity);
        var first = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < sampleCount; index++)
        {
            var capturedAt = first.AddSeconds(index * 2);
            service.Add(CreateSnapshot(capturedAt, cpuPercent: index % 101), capturedAt);
        }

        Assert.Equal(capacity, service.Samples.Count);
        Assert.Equal(first.AddSeconds((sampleCount - capacity) * 2), service.Samples[0].CapturedAt);
        Assert.Equal(first.AddSeconds((sampleCount - 1) * 2), service.Samples[^1].CapturedAt);
    }
}
