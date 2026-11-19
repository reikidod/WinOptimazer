using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class DiagnosticToolLauncherTests
{
    [Theory]
    [InlineData(DiagnosticToolKind.CoreAffinity, "CoreAffinity.bat")]
    [InlineData(DiagnosticToolKind.InputLatency, "InputLatencyOptimizer.bat")]
    public void GetToolPath_ResolvesPackagedTool(DiagnosticToolKind kind, string expectedFileName)
    {
        var path = DiagnosticToolLauncher.GetToolPath(@"C:\WinOptimizer", kind);

        Assert.Equal(Path.Combine(@"C:\WinOptimizer", "tools", "diagnostics", expectedFileName), path);
    }

    [Fact]
    public void GetToolPath_RejectsBlankApplicationDirectory()
    {
        Assert.Throws<ArgumentException>(() =>
            DiagnosticToolLauncher.GetToolPath(" ", DiagnosticToolKind.CoreAffinity));
    }

    [Fact]
    public void GetToolPath_RejectsUnknownTool()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiagnosticToolLauncher.GetToolPath(@"C:\WinOptimizer", (DiagnosticToolKind)999));
    }
}
