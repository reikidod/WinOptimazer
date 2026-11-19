using System.Diagnostics;

namespace WinOptimizer.Services;

public enum DiagnosticToolKind
{
    CoreAffinity,
    InputLatency
}

/// <summary>
/// Starts the original diagnostic tools shipped with WinOptimizer.
/// The scripts retain their own elevation, confirmation and rollback flows.
/// </summary>
internal static class DiagnosticToolLauncher
{
    internal static string GetToolPath(string applicationDirectory, DiagnosticToolKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        var fileName = kind switch
        {
            DiagnosticToolKind.CoreAffinity => "CoreAffinity.bat",
            DiagnosticToolKind.InputLatency => "InputLatencyOptimizer.bat",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный диагностический инструмент.")
        };

        return Path.GetFullPath(Path.Combine(applicationDirectory, "tools", "diagnostics", fileName));
    }

    internal static Process Launch(DiagnosticToolKind kind)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Диагностические инструменты доступны только в Windows.");

        var toolPath = GetToolPath(AppContext.BaseDirectory, kind);
        if (!File.Exists(toolPath))
            throw new FileNotFoundException($"Файл диагностического инструмента не найден: {toolPath}", toolPath);

        var commandProcessor = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = commandProcessor,
            Arguments = $"/d /c \"\"{toolPath}\"\"",
            WorkingDirectory = Path.GetDirectoryName(toolPath)!,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows не удалось запустить диагностический инструмент.");
    }
}
