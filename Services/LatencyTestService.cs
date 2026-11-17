using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinOptimizer.Services;

public sealed record LatencyMeasurement(
    double AverageMicroseconds,
    double MedianMicroseconds,
    double P95Microseconds,
    double MaximumMicroseconds,
    int SampleCount,
    bool UsedHighResolutionTimer);

/// <summary>
/// Measures Windows timer wake-up lateness. This is a scheduler/timer responsiveness
/// indicator, not an ISR/DPC execution-time or end-to-end input-latency measurement.
/// </summary>
public sealed class LatencyTestService
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint WaitObject0 = 0;
    private const int TimerPeriodMilliseconds = 1;
    private const long OneMillisecondIn100Nanoseconds = 10_000;

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(
        IntPtr timerAttributes, string? timerName, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimer(
        IntPtr timerAttributes, bool manualReset, string? timerName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public Task<LatencyMeasurement> RunAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Тест задержки поддерживается только в Windows.");
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(duration), "Длительность теста должна быть от 1 до 30 секунд.");

        return Task.Run(() => Run(duration, cancellationToken), cancellationToken);
    }

    private static LatencyMeasurement Run(TimeSpan duration, CancellationToken cancellationToken)
    {
        var timer = CreateWaitableTimer(IntPtr.Zero, false, null);
        var highResolution = false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
        {
            var highResolutionTimer = CreateWaitableTimerEx(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (highResolutionTimer != IntPtr.Zero)
            {
                if (timer != IntPtr.Zero) CloseHandle(timer);
                timer = highResolutionTimer;
                highResolution = true;
            }
        }

        if (timer == IntPtr.Zero)
            throw new InvalidOperationException("Windows не смогла создать таймер ожидания.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

        try
        {
            long dueTime = -OneMillisecondIn100Nanoseconds;
            if (!SetWaitableTimer(timer, ref dueTime, TimerPeriodMilliseconds, IntPtr.Zero, IntPtr.Zero, false))
                throw new InvalidOperationException("Не удалось настроить таймер Windows.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

            var sampleCount = Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds));
            var lateness = new double[sampleCount];
            var stopwatch = Stopwatch.StartNew();
            var start = Stopwatch.GetTimestamp();

            for (var i = 0; i < sampleCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var waitResult = WaitForSingleObject(timer, 1000);
                if (waitResult != WaitObject0)
                    throw new InvalidOperationException(waitResult == 258
                        ? "Истекло ожидание сигнала таймера Windows."
                        : $"Ошибка ожидания таймера Windows (код {Marshal.GetLastWin32Error()}).");

                var expected = start + (long)((i + 1) * (double)Stopwatch.Frequency / 1000d);
                var actual = Stopwatch.GetTimestamp();
                lateness[i] = Math.Max(0, (actual - expected) * 1_000_000d / Stopwatch.Frequency);
            }

            stopwatch.Stop();
            return Analyze(lateness, highResolution);
        }
        finally
        {
            CloseHandle(timer);
        }
    }

    internal static LatencyMeasurement Analyze(IReadOnlyCollection<double> samples, bool highResolutionTimer = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0 || samples.Any(value => !double.IsFinite(value) || value < 0))
            throw new ArgumentException("Для анализа нужны неотрицательные конечные значения задержки.", nameof(samples));

        var sorted = samples.OrderBy(value => value).ToArray();
        var median = Percentile(sorted, 0.50);
        var p95 = Percentile(sorted, 0.95);
        return new LatencyMeasurement(
            sorted.Average(),
            median,
            p95,
            sorted[^1],
            sorted.Length,
            highResolutionTimer);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}
