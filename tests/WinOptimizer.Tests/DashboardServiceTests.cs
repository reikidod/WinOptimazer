using WinOptimizer.Services;
using WinOptimizer.Views;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class DashboardServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "winoptimizer-dashboard-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SystemSnapshot_ExposesConsistentDashboardMetrics()
    {
        var snapshot = new SystemSnapshot(
            "Windows test",
            12,
            37,
            50,
            16UL * 1024 * 1024 * 1024,
            8UL * 1024 * 1024 * 1024,
            "C:\\\\",
            200L * 1024 * 1024 * 1024,
            50L * 1024 * 1024 * 1024,
            true);

        Assert.Equal("37%", snapshot.CpuSummary);
        Assert.Equal("12 логических · загрузка", snapshot.CpuLoadSubtitle);
        Assert.Equal("50% занято", snapshot.MemoryLoadSummary);
        Assert.Equal("50%", snapshot.MemoryLoadValue);
        Assert.Equal("8 ГБ из 16 ГБ", snapshot.MemoryUsageSubtitle);
        Assert.Equal(25d, snapshot.DiskFreePercent, precision: 2);
        Assert.Equal("Подключение доступно", snapshot.NetworkSummary);
        Assert.Equal("8 ГБ", snapshot.MemoryUsedText);
        Assert.Equal("16 ГБ", snapshot.MemoryTotalText);
        Assert.Equal("50 ГБ", snapshot.DiskFreeText);
        Assert.Equal("200 ГБ", snapshot.DiskTotalText);
    }

    [Fact]
    public void PerformanceHistory_KeepsOnlyNewestSamplesInChronologicalOrder()
    {
        var history = new PerformanceHistoryService(capacity: 2);
        var first = CreateSnapshot(cpu: 15, memory: 25);
        var second = CreateSnapshot(cpu: 35, memory: 45);
        var third = CreateSnapshot(cpu: 55, memory: 65);

        history.Add(first, new DateTime(2026, 1, 1, 10, 0, 0));
        history.Add(second, new DateTime(2026, 1, 1, 10, 0, 2));
        var latest = history.Add(third, new DateTime(2026, 1, 1, 10, 0, 4));

        Assert.Equal(55d, latest.CpuPercent);
        Assert.Equal(65d, latest.MemoryPercent);
        Assert.Equal(1d, latest.CpuBarOpacity);
        Assert.Equal(2, history.Samples.Count);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 2), history.Samples[0].CapturedAt);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 4), history.Samples[1].CapturedAt);
    }

    [Fact]
    public void PerformanceHistory_ClampsValuesAndKeepsUnavailableCpuUnknown()
    {
        var history = new PerformanceHistoryService();
        var point = history.Add(CreateSnapshot(cpu: 140, memory: 120) with
        {
            CpuAvailable = false,
        });

        Assert.Null(point.CpuPercent);
        Assert.Equal(2d, point.CpuBarHeight);
        Assert.Equal(0.22d, point.CpuBarOpacity);
        Assert.Equal(100d, point.MemoryPercent);
        Assert.Equal("CPU недоступен", point.CpuValueText);
        Assert.Equal("RAM 100%", point.MemoryValueText);
    }

    [Fact]
    public void PerformanceHistory_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerformanceHistoryService(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerformanceHistoryService(10_001));
    }

    private static SystemSnapshot CreateSnapshot(double cpu, uint memory) => new(
        "Windows test", 8, cpu, memory,
        16UL * 1024 * 1024 * 1024,
        8UL * 1024 * 1024 * 1024,
        "C:\\",
        100L * 1024 * 1024 * 1024,
        50L * 1024 * 1024 * 1024,
        true);

    [Fact]
    public void DriveSnapshot_FormatsCapacityAndClampsFreeSpace()
    {
        var drive = SystemMonitorService.CreateDriveSnapshot(
            "D:\\", "  Games  ", " NTFS ", 100L * 1024 * 1024 * 1024,
            25L * 1024 * 1024 * 1024, DriveType.Fixed);

        Assert.Equal("D:\\ · Games", drive.DisplayName);
        Assert.Equal("Внутренний диск · NTFS", drive.StorageTypeText);
        Assert.Equal("75 ГБ", drive.UsedText);
        Assert.Equal("25 ГБ", drive.FreeText);
        Assert.Equal("100 ГБ", drive.TotalText);
        Assert.Equal(75d, drive.UsedPercent, precision: 2);
        Assert.Equal("75 ГБ занято · 25 ГБ свободно из 100 ГБ", drive.UsageSummary);
    }

    [Fact]
    public void DriveSnapshot_IdentifiesRemovableStorageAndClampsInvalidFreeSpace()
    {
        var drive = SystemMonitorService.CreateDriveSnapshot(
            "E:\\", null, null, 10L * 1024 * 1024 * 1024,
            20L * 1024 * 1024 * 1024, DriveType.Removable);

        Assert.Equal("E:\\", drive.DisplayName);
        Assert.Equal("Съёмный накопитель · Файловая система не определена", drive.StorageTypeText);
        Assert.Equal(0L, drive.UsedPercent);
        Assert.Equal("0 Б занято · 10 ГБ свободно из 10 ГБ", drive.UsageSummary);
    }

    [Theory]
    [InlineData(-10, 26)]
    [InlineData(0, 26)]
    [InlineData(50, 13)]
    [InlineData(100, 0)]
    [InlineData(110, 0)]
    public void ArcDashOffset_ClampsProgressAndMapsToDashOffset(double progress, double expected)
    {
        Assert.Equal(expected, Motion.ArcDashOffset(progress), precision: 3);
    }

    [Fact]
    public void CreateDriveSnapshot_RejectsMissingNameAndNonPositiveCapacity()
    {
        Assert.Throws<ArgumentException>(() =>
            SystemMonitorService.CreateDriveSnapshot(" ", "Disk", "NTFS", 100, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SystemMonitorService.CreateDriveSnapshot("D:\\", "Disk", "NTFS", 0, 0));
    }

    [Fact]
    public void SystemSnapshot_ShowsUnknownWhenCpuCounterIsUnavailable()
    {
        var snapshot = new SystemSnapshot(
            "Windows test",
            8,
            0,
            40,
            16UL * 1024 * 1024 * 1024,
            10UL * 1024 * 1024 * 1024,
            "C:\\",
            100L * 1024 * 1024 * 1024,
            60L * 1024 * 1024 * 1024,
            true)
        {
            CpuAvailable = false,
        };

        Assert.Equal("—", snapshot.CpuSummary);
        Assert.Equal("Показатель недоступен", snapshot.CpuLoadSubtitle);
        Assert.True(snapshot.CpuProgressIsIndeterminate);
        Assert.Equal(0, snapshot.CpuProgressValue);
        Assert.Equal("Индикатор недоступен", snapshot.CpuProgressDescription);
    }

    [Fact]
    public void SystemSnapshot_ShowsCpuLoadWhenCounterIsAvailable()
    {
        var snapshot = new SystemSnapshot(
            "Windows test",
            8,
            37,
            40,
            16UL * 1024 * 1024 * 1024,
            10UL * 1024 * 1024 * 1024,
            "C:\\",
            100L * 1024 * 1024 * 1024,
            60L * 1024 * 1024 * 1024,
            true);

        Assert.Equal("37%", snapshot.CpuSummary);
        Assert.Equal("8 логических · загрузка", snapshot.CpuLoadSubtitle);
        Assert.False(snapshot.CpuProgressIsIndeterminate);
        Assert.Equal(37, snapshot.CpuProgressValue);
        Assert.Equal("Загрузка процессора сейчас", snapshot.CpuProgressDescription);
    }

    [Fact]
    public void OperationHistory_StoresAndReturnsNewestFirst()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.jsonl");
        var history = new OperationHistoryService(path);

        history.Record("Оптимизация", "Включён игровой режим", "Успешно");
        history.Record("Обслуживание", "Очистка временных файлов", "Удалено: 3");

        var recent = history.GetRecent(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("Очистка временных файлов", recent[0].Operation);
        Assert.Equal("Включён игровой режим", recent[1].Operation);
    }

    [Fact]
    public void OperationHistory_SkipsCorruptLines()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.jsonl");
        var history = new OperationHistoryService(path);
        history.Record("Тест", "Валидная запись", "ОК");
        File.AppendAllText(path, "{ not valid json" + Environment.NewLine);

        var recent = history.GetRecent(10);

        Assert.Single(recent);
        Assert.Equal("Валидная запись", recent[0].Operation);
    }

    [Fact]
    public void Diagnostics_DoesNotReportNoRebootWhenRegistryCheckIsUnknown()
    {
        var snapshot = new SystemSnapshot(
            "Windows test",
            8,
            35,
            35,
            16UL * 1024 * 1024 * 1024,
            10UL * 1024 * 1024 * 1024,
            "C:\\\\",
            100L * 1024 * 1024 * 1024,
            60L * 1024 * 1024 * 1024,
            true);

        var findings = new DiagnosticsService().Run(snapshot, () => RebootPendingState.Unknown);

        var rebootFinding = Assert.Single(findings, x => x.Title.Contains("перезагрузку", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DiagnosticLevel.Warning, rebootFinding.Level);
        Assert.Contains("неизвестно", rebootFinding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diagnostics_ContinuesWhenRebootProbeThrows()
    {
        var snapshot = new SystemSnapshot(
            "Windows test",
            8,
            35,
            35,
            16UL * 1024 * 1024 * 1024,
            10UL * 1024 * 1024 * 1024,
            "C:\\",
            100L * 1024 * 1024 * 1024,
            60L * 1024 * 1024 * 1024,
            true);

        var findings = new DiagnosticsService().Run(snapshot, () => throw new InvalidOperationException("Probe failed"));

        var rebootFinding = Assert.Single(findings, x => x.Title.Contains("перезагрузку", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DiagnosticLevel.Warning, rebootFinding.Level);
        Assert.Contains("неизвестно", rebootFinding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(findings, x => x.Title == "Сеть");
    }

    [Fact]
    public void Diagnostics_ReportsLowDiskAndHighMemory()
    {
        var snapshot = new SystemSnapshot(
            "Windows test",
            8,
            40,
            95,
            16UL * 1024 * 1024 * 1024,
            800UL * 1024 * 1024,
            "C:\\",
            100L * 1024 * 1024 * 1024,
            5L * 1024 * 1024 * 1024,
            false);

        var findings = new DiagnosticsService().Run(snapshot);

        Assert.Contains(findings, x => x.Level == DiagnosticLevel.Critical && x.Title.Contains("диске", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, x => x.Level == DiagnosticLevel.Warning && x.Title.Contains("памяти", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, x => x.Title.Contains("Сеть", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NetworkTrafficRateTracker_UsesCounterDeltaAndElapsedTime()
    {
        var tracker = new NetworkTrafficRateTracker();
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        var first = tracker.Update(1_000, 2_000, start);
        var second = tracker.Update(5_000, 8_000, start.AddSeconds(2));

        Assert.False(first.IsAvailable);
        Assert.Equal(2_000d, second.ReceivedBytesPerSecond);
        Assert.Equal(3_000d, second.SentBytesPerSecond);
        Assert.Equal("2 КБ/с", second.DownloadText);
        Assert.Equal("2.9 КБ/с", second.UploadText);
    }

    [Fact]
    public void NetworkTrafficRateTracker_ResetsBaselineWhenCountersRegressOrTimeIsInvalid()
    {
        var tracker = new NetworkTrafficRateTracker();
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        tracker.Update(10_000, 10_000, start);
        Assert.False(tracker.Update(100, 100, start.AddSeconds(2)).IsAvailable);
        var recovered = tracker.Update(2_100, 3_100, start.AddSeconds(3));

        Assert.Equal(2_000d, recovered.ReceivedBytesPerSecond);
        Assert.Equal(3_000d, recovered.SentBytesPerSecond);
        Assert.False(tracker.Update(3_100, 4_100, start.AddSeconds(3)).IsAvailable);
    }

    [Fact]
    public void NetworkTrafficRateTracker_RejectsNegativeCounters()
    {
        var tracker = new NetworkTrafficRateTracker();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tracker.Update(-1, 0, DateTime.UtcNow));
    }
    [Fact]
    public void DiskActivityRateTracker_UsesPerDiskCounterDeltasAndElapsedTime()
    {
        var tracker = new DiskActivityRateTracker();
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        var first = tracker.Update(1_000, 2_000, start);
        var second = tracker.Update(5_000, 8_000, start.AddSeconds(2));

        Assert.False(first.IsAvailable);
        Assert.Equal(2_000d, second.ReadBytesPerSecond);
        Assert.Equal(3_000d, second.WriteBytesPerSecond);
        Assert.Equal("2 КБ/с", second.ReadText);
        Assert.Equal("2.9 КБ/с", second.WriteText);
    }

    [Fact]
    public void DiskActivityRateTracker_ResetsBaselineAfterCounterRegressionOrUnavailableData()
    {
        var tracker = new DiskActivityRateTracker();
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        tracker.Update(10_000, 10_000, start);
        Assert.False(tracker.Update(100, 100, start.AddSeconds(2)).IsAvailable);
        var recovered = tracker.Update(2_100, 3_100, start.AddSeconds(3));
        Assert.Equal(2_000d, recovered.ReadBytesPerSecond);
        Assert.Equal(3_000d, recovered.WriteBytesPerSecond);

        Assert.False(tracker.Update(null, 4_100, start.AddSeconds(4)).IsAvailable);
        Assert.False(tracker.Update(4_100, 5_100, start.AddSeconds(5)).IsAvailable);
        var afterGap = tracker.Update(6_100, 7_100, start.AddSeconds(6));
        Assert.Equal(2_000d, afterGap.ReadBytesPerSecond);
        Assert.Equal(2_000d, afterGap.WriteBytesPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31)]
    public void DiskActivityRateTracker_RejectsInvalidElapsedIntervals(int elapsedSeconds)
    {
        var tracker = new DiskActivityRateTracker();
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        tracker.Update(1_000, 1_000, start);
        Assert.False(tracker.Update(2_000, 2_000, start.AddSeconds(elapsedSeconds)).IsAvailable);
    }

    [Fact]
    public void DiskActivityRateTracker_RejectsNegativeCounters()
    {
        var tracker = new DiskActivityRateTracker();
        var now = DateTime.UtcNow;

        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Update(-1, 0, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Update(0, -1, now));
    }

    [Fact]
    public void PhysicalDiskActivitySnapshot_ShowsRatesAndKeepsWarmupUnavailable()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var measured = new PhysicalDiskActivitySnapshot("0 C:", 1_048_576, 512, now);
        var warmingUp = new PhysicalDiskActivitySnapshot("1 D:", null, null, now);

        Assert.True(measured.IsAvailable);
        Assert.Equal("1 МБ/с", measured.ReadText);
        Assert.Equal("512 Б/с", measured.WriteText);
        Assert.False(warmingUp.IsAvailable);
        Assert.Equal("Измерение…", warmingUp.ReadText);
        Assert.Equal("Измерение…", warmingUp.WriteText);
    }

}
