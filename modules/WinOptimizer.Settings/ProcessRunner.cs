using System.Diagnostics;

namespace WinOptimizer.Services;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessRunner
{
    /// <summary>Запускает консольную утилиту без окна и ждёт завершения. Вызывать не из UI-потока.</summary>
    public static ProcessResult Run(string fileName, string arguments, int timeoutMs = 15_000)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Не удалось запустить {fileName}.");

        // Потоки читаются параллельно с ожиданием, иначе переполнение буфера блокирует процесс.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Процесс успел завершиться сам.
            }

            throw new TimeoutException($"{Path.GetFileName(fileName)} не ответил за {timeoutMs / 1000} с.");
        }

        process.WaitForExit(); // дожидаемся дочитывания асинхронных потоков
        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
