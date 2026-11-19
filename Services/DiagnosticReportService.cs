using System.Text.Json;

namespace WinOptimizer.Services;

internal sealed record DiagnosticRunReport(
    string Tool,
    string Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int? ExitCode,
    bool Cancelled,
    string Output);

internal static class DiagnosticReportService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    internal static string Serialize(DiagnosticRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(report.Tool))
            throw new ArgumentException("В отчёте не указан инструмент.", nameof(report));
        if (string.IsNullOrWhiteSpace(report.Mode))
            throw new ArgumentException("В отчёте не указан режим.", nameof(report));
        return JsonSerializer.Serialize(report, Options);
    }
}
