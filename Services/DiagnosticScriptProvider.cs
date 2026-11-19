using System.Reflection;
using System.Text;

namespace WinOptimizer.Services;

public enum DiagnosticToolKind
{
    CoreAffinity,
    InputLatency
}

/// <summary>
/// Supplies the original diagnostic PowerShell logic from embedded application resources.
/// No BAT file is executed or copied to the application output directory.
/// </summary>
internal static class DiagnosticScriptProvider
{
    private const string CoreAffinityResource = "WinOptimizer.Diagnostics.CoreAffinity.bat";
    private const string InputLatencyResource = "WinOptimizer.Diagnostics.InputLatency.ps1";
    private const string CoreAffinityMarker = "#PSBEGIN";

    internal static string ExtractScript(DiagnosticToolKind kind)
    {
        var resource = kind switch
        {
            DiagnosticToolKind.CoreAffinity => CoreAffinityResource,
            DiagnosticToolKind.InputLatency => InputLatencyResource,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный диагностический инструмент.")
        };

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"Встроенный сценарий {resource} не найден.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();

        if (kind == DiagnosticToolKind.CoreAffinity)
        {
            var markerIndex = content.LastIndexOf(CoreAffinityMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
                throw new InvalidDataException("В CoreAffinity не найден раздел PowerShell.");
            content = content[(markerIndex + CoreAffinityMarker.Length)..].TrimStart('\r', '\n');
        }

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException($"Сценарий {kind} пуст.");
        return content;
    }

    internal static string MaterializeScript(DiagnosticToolKind kind)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinOptimizer", "EmbeddedDiagnostics");
        Directory.CreateDirectory(directory);

        var fileName = kind == DiagnosticToolKind.CoreAffinity
            ? "CoreAffinity.internal.ps1"
            : "InputLatencyOptimizer.internal.ps1";
        var path = Path.Combine(directory, fileName);
        var content = ExtractScript(kind);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.Move(temporary, path, overwrite: true);
        return path;
    }
}
