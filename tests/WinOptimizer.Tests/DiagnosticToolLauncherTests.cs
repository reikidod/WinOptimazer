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
    public void PackagedScripts_PreserveCoreAffinityAndInputLatencyWorkflows()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "tools", "diagnostics");
        var coreAffinity = File.ReadAllText(Path.Combine(root, "CoreAffinity.bat"));
        var inputLatencyMenu = File.ReadAllText(Path.Combine(root, "InputLatencyOptimizer.bat"));
        var inputLatency = File.ReadAllText(Path.Combine(root, "InputLatencyOptimizer.ps1"));

        Assert.Contains("FPS", coreAffinity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetSystemCpuSetInformation", coreAffinity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restore", coreAffinity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("autodetect", inputLatencyMenu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safeapply", inputLatencyMenu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HidRawSender", inputLatency, StringComparison.Ordinal);
        Assert.Contains("InputProbe", inputLatency, StringComparison.Ordinal);
        Assert.Contains("rollback", inputLatency, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USB selective suspend", inputLatency, StringComparison.OrdinalIgnoreCase);
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
