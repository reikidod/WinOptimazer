using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WinOptimizer.GameBoostService;

/// <summary>Pure safety rules shared by the service runtime and regression tests.</summary>
public static class GameBoostPolicy
{
    public const string BalancedMode = "balanced";
    public const string MaximumMode = "maximum";

    private static readonly Regex PowerSchemePattern = new(
        @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RunningStatePattern = new(
        @"STATE\s*:\s*4\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsValidMode(string? mode) =>
        string.Equals(mode, BalancedMode, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, MaximumMode, StringComparison.OrdinalIgnoreCase);

    public static ProcessPriorityClass TargetPriority(string? mode) =>
        string.Equals(mode, MaximumMode, StringComparison.OrdinalIgnoreCase)
            ? ProcessPriorityClass.High
            : ProcessPriorityClass.AboveNormal;

    public static bool ShouldPromotePriority(ProcessPriorityClass priority) =>
        priority is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Normal;

    public static bool IsPriorityBelow(ProcessPriorityClass current, ProcessPriorityClass target) =>
        PriorityRank(current) < PriorityRank(target);

    private static int PriorityRank(ProcessPriorityClass priority) => priority switch
    {
        ProcessPriorityClass.Idle => 0,
        ProcessPriorityClass.BelowNormal => 1,
        ProcessPriorityClass.Normal => 2,
        ProcessPriorityClass.AboveNormal => 3,
        ProcessPriorityClass.High => 4,
        ProcessPriorityClass.RealTime => 5,
        _ => 2
    };

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

        try
        {
            var fullPath = Path.GetFullPath(path);
            var windowsRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            // The service must never promote operating-system executables or core security processes.
            if (fullPath.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            return File.Exists(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
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
