using System.Text.Json;
using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class DiagnosticReportServiceTests
{
    [Fact]
    public void Serialize_PreservesToolModeTimingExitCodeAndOutput()
    {
        var started = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        var finished = started.AddSeconds(12);
        var report = new DiagnosticRunReport(
            "InputLatency", "measure", started, finished, 0, false,
            "Polling rate: 1000 Hz\r\nEvents: 5000");

        var json = DiagnosticReportService.Serialize(report);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("InputLatency", root.GetProperty("Tool").GetString());
        Assert.Equal("measure", root.GetProperty("Mode").GetString());
        Assert.Equal(0, root.GetProperty("ExitCode").GetInt32());
        Assert.False(root.GetProperty("Cancelled").GetBoolean());
        Assert.Contains("1000 Hz", root.GetProperty("Output").GetString());
        Assert.Equal(finished, root.GetProperty("FinishedAt").GetDateTimeOffset());
    }

    [Fact]
    public void Serialize_RejectsMissingTool()
    {
        var report = new DiagnosticRunReport("", "scan", DateTimeOffset.Now, null, null, false, "output");
        Assert.Throws<ArgumentException>(() => DiagnosticReportService.Serialize(report));
    }
}
