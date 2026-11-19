using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class DiagnosticScriptProviderTests
{
    [Fact]
    public void CoreAffinity_ExtractsPowerShellWithoutBatchBootstrap()
    {
        var script = DiagnosticScriptProvider.ExtractScript(DiagnosticToolKind.CoreAffinity);

        Assert.Contains("GetSystemCpuSetInformation", script, StringComparison.Ordinal);
        Assert.Contains("FPS", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Read-Host", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@echo off", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InputLatency_ContainsOriginalModesAndSafetyFlows()
    {
        var script = DiagnosticScriptProvider.ExtractScript(DiagnosticToolKind.InputLatency);

        Assert.Contains("'autodetect'", script, StringComparison.Ordinal);
        Assert.Contains("'dongle'", script, StringComparison.Ordinal);
        Assert.Contains("'safeapply'", script, StringComparison.Ordinal);
        Assert.Contains("'rollback'", script, StringComparison.Ordinal);
        Assert.Contains("HidRawSender", script, StringComparison.Ordinal);
        Assert.Contains("InputProbe", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterializeScript_WritesInternalPowerShellScript()
    {
        var path = DiagnosticScriptProvider.MaterializeScript(DiagnosticToolKind.InputLatency);

        Assert.EndsWith("InputLatencyOptimizer.internal.ps1", path, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
        Assert.Contains("param(", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
