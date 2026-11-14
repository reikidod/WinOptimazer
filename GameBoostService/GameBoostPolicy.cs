using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WinOptimizer.GameBoostService;

/// <summary>Pure safety rules shared by the service runtime and regression tests.</summary>
public static class GameBoostPolicy
{
    private static readonly Regex PowerSchemePattern = new(
        @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RunningStatePattern = new(
        @"STATE\s*:\s*4\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool ShouldPromotePriority(ProcessPriorityClass priority) =>
        priority is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Normal;

    public static bool IsSameProcessInstance(int expectedProcessId, DateTime expectedStartTimeUtc,
        int actualProcessId, DateTime actualStartTimeUtc) =>
        expectedProcessId == actualProcessId &&
        expectedStartTimeUtc.ToUniversalTime() == actualStartTimeUtc.ToUniversalTime();

    public static bool IsSafeExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            return false;
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return false; }
    }

    public static string? ExtractPowerSchemeGuid(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = PowerSchemePattern.Match(output);
        return match.Success ? match.Value : null;
    }

    public static bool IsServiceRunning(string? scQueryOutput) =>
        !string.IsNullOrWhiteSpace(scQueryOutput) && RunningStatePattern.IsMatch(scQueryOutput);
}
