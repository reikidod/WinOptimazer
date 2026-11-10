using WinOptimizer.Infrastructure;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class PageNavigationHistoryTests
{
    [Fact]
    public void BackAndForward_RestoreVisitedPagesInOrder()
    {
        var history = new PageNavigationHistory<string>();
        history.Record("Dashboard", "Tweaks");
        history.Record("Tweaks", "Games");

        Assert.True(history.TryBack("Games", _ => true, out var previous));
        Assert.Equal("Tweaks", previous);
        Assert.True(history.TryBack("Tweaks", _ => true, out previous));
        Assert.Equal("Dashboard", previous);

        Assert.True(history.TryForward("Dashboard", _ => true, out var next));
        Assert.Equal("Tweaks", next);
        Assert.True(history.TryForward("Tweaks", _ => true, out next));
        Assert.Equal("Games", next);
    }

    [Fact]
    public void NewNavigation_ClearsForwardHistory()
    {
        var history = new PageNavigationHistory<string>();
        history.Record("Dashboard", "Tweaks");
        history.Record("Tweaks", "Games");
        Assert.True(history.TryBack("Games", _ => true, out var previous));
        Assert.Equal("Tweaks", previous);

        history.Record("Tweaks", "Settings");

        Assert.False(history.TryForward("Settings", _ => true, out _));
    }

    [Fact]
    public void Back_SkipsPagesThatAreNoLongerVisible()
    {
        var history = new PageNavigationHistory<string>();
        history.Record("Dashboard", "Hidden");
        history.Record("Hidden", "Games");

        Assert.True(history.TryBack("Games", page => page != "Hidden", out var previous));
        Assert.Equal("Dashboard", previous);
    }

    [Fact]
    public void RecordingSamePage_DoesNotCreateHistoryEntry()
    {
        var history = new PageNavigationHistory<string>();
        history.Record("Dashboard", "Dashboard");

        Assert.False(history.TryBack("Dashboard", _ => true, out _));
    }
}
