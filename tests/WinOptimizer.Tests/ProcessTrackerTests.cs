using System.Windows;
using WinOptimizer.Services;
using WinOptimizer.Views;
using Xunit;

namespace WinOptimizer.Tests;

public class ProcessTrackerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ProcessSample Sample(int pid, string name, double cpuSeconds, long memMb = 100, long start = 1) =>
        new(pid, name, start, TimeSpan.FromSeconds(cpuSeconds), memMb * 1024 * 1024);

    [Fact]
    public void Cpu_IsDeltaOverElapsedPerCore()
    {
        var tracker = new ProcessTracker(cpuCores: 4);
        tracker.Update(new[] { Sample(10, "game", 0) }, T0);
        tracker.Update(new[] { Sample(10, "game", 2) }, T0.AddSeconds(2));

        // 2 с процессорного времени за 2 с на 4 ядрах = 25 %
        Assert.Equal(25, tracker.GetActive(10).Single().CpuPercent, 3);
    }

    [Fact]
    public void Cpu_IsClampedTo100()
    {
        var tracker = new ProcessTracker(cpuCores: 1);
        tracker.Update(new[] { Sample(10, "a", 0) }, T0);
        tracker.Update(new[] { Sample(10, "a", 50) }, T0.AddSeconds(2));

        Assert.Equal(100, tracker.GetActive(10).Single().CpuPercent);
    }

    [Fact]
    public void FirstSample_ReportsNoExitedProcesses()
    {
        var tracker = new ProcessTracker(4);
        tracker.Update(new[] { Sample(1, "a", 0), Sample(2, "b", 0) }, T0);

        Assert.Empty(tracker.Exited);
        Assert.Equal(2, tracker.ActiveCount);
    }

    [Fact]
    public void VanishedProcess_MovesToExited_WithLastNumbers()
    {
        var tracker = new ProcessTracker(2);
        tracker.Update(new[] { Sample(7, "tool", 0, memMb: 50) }, T0);
        tracker.Update(new[] { Sample(7, "tool", 1, memMb: 300) }, T0.AddSeconds(2));
        tracker.Update(Array.Empty<ProcessSample>(), T0.AddSeconds(4));

        var exited = Assert.Single(tracker.Exited);
        Assert.Equal("tool", exited.Name);
        Assert.Equal(7, exited.Pid);
        Assert.Equal(25, exited.LastCpuPercent, 3);
        Assert.Equal(300L * 1024 * 1024, exited.PeakWorkingSetBytes);
        Assert.Equal(T0.AddSeconds(4), exited.ExitedAtUtc);
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public void ReusedPid_WithNewStartTime_IsTreatedAsNewProcess()
    {
        var tracker = new ProcessTracker(2);
        tracker.Update(new[] { Sample(5, "old", 0, start: 100) }, T0);
        tracker.Update(new[] { Sample(5, "new", 0, start: 200) }, T0.AddSeconds(2));

        Assert.Equal("old", Assert.Single(tracker.Exited).Name);
        Assert.Equal("new", tracker.GetActive(10).Single().Name);
    }

    [Fact]
    public void SameNames_AreGrouped_AndSortedByCpuThenMemory()
    {
        var tracker = new ProcessTracker(1);
        tracker.Update(new[]
        {
            Sample(1, "chrome", 0, 100), Sample(2, "chrome", 0, 200), Sample(3, "calc", 0, 10), Sample(4, "idle-app", 0, 900),
        }, T0);
        tracker.Update(new[]
        {
            Sample(1, "chrome", 0.2, 100), Sample(2, "chrome", 0.2, 200), Sample(3, "calc", 1.0, 10), Sample(4, "idle-app", 0, 900),
        }, T0.AddSeconds(2));

        var active = tracker.GetActive(10);
        Assert.Equal(new[] { "calc", "chrome", "idle-app" }, active.Select(a => a.Name));

        var chrome = active.Single(a => a.Name == "chrome");
        Assert.Equal(2, chrome.Count);
        Assert.Equal(300L * 1024 * 1024, chrome.WorkingSetBytes);
        Assert.Equal(20, chrome.CpuPercent, 3);
    }

    [Fact]
    public void GetActive_RespectsTopLimit()
    {
        var tracker = new ProcessTracker(2);
        tracker.Update(Enumerable.Range(1, 10).Select(i => Sample(i, $"p{i}", 0)).ToList(), T0);

        Assert.Equal(3, tracker.GetActive(3).Count);
    }

    [Fact]
    public void ExitedList_IsCapped_AndNewestFirst()
    {
        var tracker = new ProcessTracker(2, maxExited: 3);
        tracker.Update(Enumerable.Range(1, 5).Select(i => Sample(i, $"p{i}", 0)).ToList(), T0);
        for (var i = 1; i <= 5; i++)
        {
            tracker.Update(Enumerable.Range(i + 1, 5 - i).Select(n => Sample(n, $"p{n}", 0)).ToList(), T0.AddSeconds(i));
        }

        Assert.Equal(new[] { "p5", "p4", "p3" }, tracker.Exited.Select(e => e.Name));
    }

    [Fact]
    public void UnreadableCpuTime_DoesNotThrow_AndShowsZero()
    {
        var tracker = new ProcessTracker(4);
        var protectedProcess = new ProcessSample(4, "System", 0, null, 10 * 1024 * 1024);
        tracker.Update(new[] { protectedProcess }, T0);
        tracker.Update(new[] { protectedProcess }, T0.AddSeconds(2));

        Assert.Equal(0, tracker.GetActive(5).Single().CpuPercent);
    }

    [Fact]
    public void Totals_SumAllProcesses()
    {
        var tracker = new ProcessTracker(1);
        tracker.Update(new[] { Sample(1, "a", 0, 100), Sample(2, "b", 0, 200) }, T0);
        tracker.Update(new[] { Sample(1, "a", 0.5, 100), Sample(2, "b", 0.25, 200) }, T0.AddSeconds(1));

        var (cpu, memory) = tracker.GetTotals();
        Assert.Equal(75, cpu, 3);
        Assert.Equal(300L * 1024 * 1024, memory);
    }

    [Fact]
    public void Sampler_ReadsCurrentProcess_WithoutThrowing()
    {
        var samples = ProcessSampler.Take();

        Assert.Contains(samples, s => s.Pid == Environment.ProcessId);
        Assert.DoesNotContain(samples, s => s.Pid == 0);
    }

    [Theory]
    [InlineData(0, "0 МБ")]
    [InlineData(256L * 1024 * 1024, "256 МБ")]
    [InlineData(1536L * 1024 * 1024, "1,5 ГБ")]
    public void Memory_IsFormattedInRussian(long bytes, string expected) => Assert.Equal(expected, ProcessFormat.Memory(bytes));

    [Theory]
    [InlineData(0, "0,0 %")]
    [InlineData(12.34, "12,3 %")]
    [InlineData(100, "100,0 %")]
    public void Cpu_IsFormattedWithComma(double percent, string expected) => Assert.Equal(expected, ProcessFormat.Cpu(percent));

    [Theory]
    [InlineData(5, "5 с")]
    [InlineData(125, "2 мин 5 с")]
    [InlineData(3 * 3600 + 120, "3 ч 2 мин")]
    [InlineData(2 * 86400 + 5 * 3600, "2 д 5 ч")]
    public void Duration_IsReadable(int seconds, string expected) => Assert.Equal(expected, ProcessFormat.Duration(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0, Visibility.Visible)]
    [InlineData(3, Visibility.Collapsed)]
    public void EmptyHint_ShowsOnlyForEmptyList(int count, Visibility expected) =>
        Assert.Equal(expected, new CountToVisibilityConverter().Convert(count, typeof(Visibility), null!, System.Globalization.CultureInfo.InvariantCulture));
}
