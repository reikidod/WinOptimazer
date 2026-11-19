using System.Text.Json;

namespace WinOptimizer.Services;

public sealed record GpuBenchmarkMetrics(
    double AverageFps,
    double OnePercentLowFps,
    double P99FrameTimeMs,
    int FrameCount);

/// <summary>
/// Parses liblava's documented benchmark JSON output and derives frame-time metrics.
/// </summary>
public static class LiblavaBenchmarkParser
{
    public static GpuBenchmarkMetrics Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("frames", out var framesElement) ||
            framesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("В JSON liblava отсутствует массив frames.");

        var frameTimes = framesElement.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.Number)
            .Select(value => value.GetDouble())
            .Where(value => double.IsFinite(value) && value > 0 && value < 1000)
            .ToArray();

        if (frameTimes.Length < 100)
            throw new InvalidDataException(
                $"Недостаточно кадров для достоверной оценки: {frameTimes.Length}; требуется не менее 100.");

        var averageFrameTime = frameTimes.Average();
        var slowestCount = Math.Max(1, (int)Math.Ceiling(frameTimes.Length * 0.01));
        var onePercentLowFrameTime = frameTimes
            .OrderByDescending(value => value)
            .Take(slowestCount)
            .Average();
        var p99FrameTime = frameTimes
            .OrderBy(value => value)
            .ElementAt((int)Math.Ceiling(frameTimes.Length * 0.99) - 1);

        return new GpuBenchmarkMetrics(
            1000.0 / averageFrameTime,
            1000.0 / onePercentLowFrameTime,
            p99FrameTime,
            frameTimes.Length);
    }
}
