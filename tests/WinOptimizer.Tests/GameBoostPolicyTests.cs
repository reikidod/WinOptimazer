using System.Diagnostics;
using WinOptimizer.GameBoostService;

namespace WinOptimizer.Tests;

public sealed class GameBoostPolicyTests
{
    [Theory]
    [InlineData(ProcessPriorityClass.Idle, true)]
    [InlineData(ProcessPriorityClass.BelowNormal, true)]
    [InlineData(ProcessPriorityClass.Normal, true)]
    [InlineData(ProcessPriorityClass.AboveNormal, false)]
    [InlineData(ProcessPriorityClass.High, false)]
    [InlineData(ProcessPriorityClass.RealTime, false)]
    public void PriorityPolicy_OnlyPromotesProcessesThatAreNotAlreadyElevated(
        ProcessPriorityClass current, bool expected) =>
        Assert.Equal(expected, GameBoostPolicy.ShouldPromotePriority(current));

    [Fact]
    public void Recovery_RejectsReusedProcessId()
    {
        var originalStart = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        var replacementStart = originalStart.AddSeconds(1);
        Assert.True(GameBoostPolicy.IsSameProcessInstance(42, originalStart, 42, originalStart));
        Assert.False(GameBoostPolicy.IsSameProcessInstance(42, originalStart, 42, replacementStart));
        Assert.False(GameBoostPolicy.IsSameProcessInstance(42, originalStart, 43, originalStart));
    }

    [Fact]
    public void PowerPlanParser_ExtractsGuidFromWindowsOutput()
    {
        const string output = "Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)";
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", GameBoostPolicy.ExtractPowerSchemeGuid(output));
        Assert.Null(GameBoostPolicy.ExtractPowerSchemeGuid("Power scheme information unavailable"));
    }

    [Theory]
    [InlineData("SERVICE_NAME: WinOptimizerGameBoost\r\n        STATE              : 4  RUNNING", true)]
    [InlineData("SERVICE_NAME: WinOptimizerGameBoost\r\n        STATE              : 1  STOPPED", false)]
    [InlineData("SERVICE_NAME: WinOptimizerGameBoost\r\n        STATE              : 2  START_PENDING", false)]
    [InlineData("", false)]
    public void ServiceStateParser_OnlyAcceptsRunningState(string output, bool expected) =>
        Assert.Equal(expected, GameBoostPolicy.IsServiceRunning(output));

    [Fact]
    public void ExecutableValidation_RequiresAnExistingAbsoluteExe()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            Assert.False(GameBoostPolicy.IsSafeExecutablePath(path));
            File.WriteAllText(path, "test");
            Assert.True(GameBoostPolicy.IsSafeExecutablePath(path));
            Assert.False(GameBoostPolicy.IsSafeExecutablePath(Path.ChangeExtension(path, ".txt")));
            Assert.False(GameBoostPolicy.IsSafeExecutablePath("relative\\game.exe"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
