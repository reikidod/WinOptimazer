using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class TaskSchedulerServiceTests
{
    [Fact]
    public void Filter_MatchesTaskNameAndPathWithoutCaseSensitivity()
    {
        var task = CreateTask();

        Assert.True(ScheduledTaskFilter.Matches(task, "windows update", "Все"));
        Assert.True(ScheduledTaskFilter.Matches(task, "UpdateOrchestrator", "Все"));
        Assert.False(ScheduledTaskFilter.Matches(task, "defender", "Все"));
    }

    [Fact]
    public void Filter_SeparatesEnabledAndDisabledTasks()
    {
        var enabled = CreateTask(enabled: true);
        var disabled = CreateTask(enabled: false);

        Assert.True(ScheduledTaskFilter.Matches(enabled, null, "Включены"));
        Assert.False(ScheduledTaskFilter.Matches(disabled, null, "Включены"));
        Assert.True(ScheduledTaskFilter.Matches(disabled, null, "Отключены"));
        Assert.False(ScheduledTaskFilter.Matches(enabled, null, "Отключены"));
    }

    [Fact]
    public void Filter_CanShowOnlyRunningTasks()
    {
        var running = CreateTask(state: (int)ScheduledTaskState.Running);
        var ready = CreateTask(state: (int)ScheduledTaskState.Ready);

        Assert.True(ScheduledTaskFilter.Matches(running, null, "Выполняются"));
        Assert.False(ScheduledTaskFilter.Matches(ready, null, "Выполняются"));
        Assert.True(ScheduledTaskFilter.Matches(ready, null, "Готовы"));
    }

    [Fact]
    public void Filter_CanSearchByPublisher()
    {
        var task = CreateTask(path: @"\NVIDIA\NvTmRep", author: "NVIDIA Corporation");

        Assert.True(ScheduledTaskFilter.Matches(task, "nvidia corporation", "Все"));
    }

    [Fact]
    public void Category_RecognizesMicrosoftWindowsTasksAsSystem()
    {
        var task = CreateTask(author: "Microsoft Corporation");

        Assert.Equal("Системная", task.CategoryLabel);
        Assert.True(ScheduledTaskFilter.Matches(task, null, "Системные"));
    }

    [Fact]
    public void Category_RecognizesHardwareVendorTasksAsDriver()
    {
        var task = CreateTask(path: @"\NVIDIA\NvTmRep", author: "NVIDIA Corporation");

        Assert.Equal("Драйвер", task.CategoryLabel);
        Assert.True(ScheduledTaskFilter.Matches(task, null, "Драйверы"));
    }

    [Fact]
    public void Category_UsesUserFallbackForThirdPartyTasks()
    {
        var task = CreateTask(path: @"\Acme\Updater", author: "Acme Software");

        Assert.Equal("Пользовательская", task.CategoryLabel);
        Assert.True(ScheduledTaskFilter.Matches(task, null, "Пользовательские"));
    }

    private static ScheduledTaskEntry CreateTask(
        bool enabled = true,
        int state = (int)ScheduledTaskState.Ready,
        string path = @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan",
        string? author = null) =>
        new(
            "Schedule Scan",
            path,
            enabled,
            state,
            new DateTime(2026, 11, 10, 8, 30, 0),
            null,
            0,
            author);
}
