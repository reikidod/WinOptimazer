using System.Diagnostics;
using Xunit;
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

    [Theory]
    [InlineData("balanced", ProcessPriorityClass.AboveNormal)]
    [InlineData("maximum", ProcessPriorityClass.High)]
    public void PerGameMode_UsesBoundedPriority(string mode, ProcessPriorityClass expected) =>
        Assert.Equal(expected, GameBoostPolicy.TargetPriority(mode));

    [Theory]
    [InlineData(ProcessPriorityClass.Normal, ProcessPriorityClass.AboveNormal, true)]
    [InlineData(ProcessPriorityClass.AboveNormal, ProcessPriorityClass.High, true)]
    [InlineData(ProcessPriorityClass.High, ProcessPriorityClass.AboveNormal, false)]
    [InlineData(ProcessPriorityClass.RealTime, ProcessPriorityClass.High, false)]
    public void PriorityComparison_OnlyRaisesPriorityWhenBelowTarget(
        ProcessPriorityClass current, ProcessPriorityClass target, bool expected) =>
        Assert.Equal(expected, GameBoostPolicy.IsPriorityBelow(current, target));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    [InlineData("not-a-guid")]
    public void PowerPlanIdValidation_AllowsOnlyEmptyOrGuid(string? powerPlanId)
    {
        var expected = string.IsNullOrWhiteSpace(powerPlanId) ||
                       Guid.TryParse(powerPlanId, out _);
        Assert.Equal(expected, GameBoostPolicy.IsValidPowerPlanId(powerPlanId));
    }

    [Fact]
    public void Policy_RejectsInvalidModeAndOperatingSystemExecutables()
    {
        Assert.False(GameBoostPolicy.IsValidMode("disable-defender"));
        Assert.True(GameBoostPolicy.IsValidMode(GameBoostPolicy.MaximumMode));
        Assert.False(GameBoostPolicy.IsSafeExecutablePath(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
    }

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
