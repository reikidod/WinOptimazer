using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace WinOptimizer.Services;

public enum DiagnosticToolKind
{
    CoreAffinity,
    InputLatency
}

/// <summary>
/// Supplies trusted diagnostic scripts from the application assembly.
/// Materialized copies are stored in a ProgramData directory restricted to SYSTEM
/// and Administrators; ordinary users receive read/execute access only.
/// </summary>
internal static class DiagnosticScriptProvider
{
    private const string CoreAffinityResource = "WinOptimizer.Diagnostics.CoreAffinity.ps1";
    private const string InputLatencyResource = "WinOptimizer.Diagnostics.InputLatency.ps1";

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

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException($"Сценарий {kind} пуст.");
        return content;
    }

    internal static string MaterializeScript(DiagnosticToolKind kind)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var applicationData = Path.Combine(programData, "WinOptimizer");
        var directory = Path.Combine(applicationData, "EmbeddedDiagnostics");
        return MaterializeScriptCore(kind, directory, protectDirectory: true);
    }

    internal static string MaterializeScriptForTests(DiagnosticToolKind kind, string directory) =>
        MaterializeScriptCore(kind, directory, protectDirectory: false);

    private static string MaterializeScriptCore(DiagnosticToolKind kind, string directory, bool protectDirectory)
    {
        var parentDirectory = Path.GetDirectoryName(directory)
            ?? throw new InvalidOperationException("Не удалось определить каталог диагностических сценариев.");
        Directory.CreateDirectory(parentDirectory);
        EnsureNoReparsePoint(parentDirectory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoint(directory);

        var fileName = kind switch
        {
            DiagnosticToolKind.CoreAffinity => "CoreAffinity.internal.ps1",
            DiagnosticToolKind.InputLatency => "InputLatencyOptimizer.internal.ps1",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный диагностический инструмент.")
        };
        var path = Path.Combine(directory, fileName);
        EnsureNoReparsePoint(path, allowMissing: true);

        var content = ExtractScript(kind);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(content);
        var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (protectDirectory) RestrictDirectory(directory);

            // Verify the on-disk copy only after its parent ACL has been restricted.
            // A mismatch means the file must never be passed to elevated PowerShell.
            var written = File.ReadAllBytes(temporary);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(written)))
                throw new CryptographicException("Контрольная сумма встроенного сценария не совпадает с записанной копией.");

            File.Move(temporary, path, overwrite: true);
            EnsureNoReparsePoint(path);
            var finalBytes = File.ReadAllBytes(path);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(finalBytes)))
                throw new CryptographicException("Проверка целостности диагностического сценария после установки не пройдена.");

            return path;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* best-effort cleanup; never hide the original failure */ }
        }
    }

    private static void RestrictDirectory(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentSid = identity.User?.Value
            ?? throw new InvalidOperationException("Не удалось определить SID текущего пользователя.");
        var icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        if (!File.Exists(icacls))
            throw new FileNotFoundException("Не найдена системная утилита icacls.exe.", icacls);

        var start = new ProcessStartInfo
        {
            FileName = icacls,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add("/inheritance:r");
        start.ArgumentList.Add("/grant:r");
        start.ArgumentList.Add("*S-1-5-18:(OI)(CI)F");
        start.ArgumentList.Add("*S-1-5-32-544:(OI)(CI)F");
        start.ArgumentList.Add($"*{currentSid}:(OI)(CI)RX");
        start.ArgumentList.Add("/T");
        start.ArgumentList.Add("/C");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Не удалось запустить icacls.exe для защиты каталога сценариев.");
        if (!process.WaitForExit(10_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Истекло время настройки ACL каталога диагностических сценариев.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
            throw new UnauthorizedAccessException($"Не удалось ограничить доступ к диагностическим сценариям (icacls {process.ExitCode}). {output} {error}".Trim());
    }

    private static void EnsureNoReparsePoint(string path, bool allowMissing = false)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            if (allowMissing) return;
            throw new FileNotFoundException("Защищённый диагностический файл не найден.", path);
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Отказ от использования пути с reparse point: {path}");
    }
}
