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
    // sc.exe localizes the label ("STATE" / "СОСТОЯНИЕ") but never the "<number> <STATE_NAME>" pair.
    private static readonly Regex ServiceStatePattern = new(
        @":\s*(?<code>[1-7])\s+(?:STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsValidMode(string? mode) =>
        string.Equals(mode, BalancedMode, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, MaximumMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Empty means keep the user's current power plan; otherwise only a GUID is accepted.</summary>
    public static bool IsValidPowerPlanId(string? powerPlanId) =>
        string.IsNullOrWhiteSpace(powerPlanId) || Guid.TryParse(powerPlanId, out _);

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

    /// <summary>Returns the SERVICE_STATUS code (1 stopped … 4 running) from `sc query` output in any Windows language.</summary>
    public static int? ParseServiceState(string? scQueryOutput)
    {
        if (string.IsNullOrWhiteSpace(scQueryOutput)) return null;
        var match = ServiceStatePattern.Match(scQueryOutput);
        return match.Success ? int.Parse(match.Groups["code"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public static bool IsServiceRunning(string? scQueryOutput) => ParseServiceState(scQueryOutput) == 4;

    /// <summary>Console tools (sc, icacls, powercfg…) print in the OEM code page (866 on Russian Windows), not UTF-8.</summary>
    public static System.Text.Encoding ConsoleEncoding { get; } = CreateConsoleEncoding();

    private static System.Text.Encoding CreateConsoleEncoding()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(OemCodePage());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return System.Text.Encoding.UTF8;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetOEMCP")]
    private static extern uint GetOemCodePage();

    private static int OemCodePage() => (int)GetOemCodePage();
}
