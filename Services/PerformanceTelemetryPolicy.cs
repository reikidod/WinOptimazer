namespace WinOptimizer.Services;

/// <summary>Единые интервалы опроса источников Performance Center.</summary>
public static class PerformanceTelemetryPolicy
{
    public static TimeSpan DashboardRefreshInterval { get; } = TimeSpan.FromSeconds(2);
    public static TimeSpan DeviceEnumerationRefreshInterval { get; } = TimeSpan.FromSeconds(15);
    public static TimeSpan TemperatureRefreshInterval { get; } = TimeSpan.FromSeconds(5);
    public static TimeSpan MaximumCounterSampleGap { get; } = TimeSpan.FromSeconds(30);
}
