using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinOptimizer.Services;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessRunner
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>
    /// Кодировка консольного вывода системных утилит (powercfg, netsh…): на русской Windows это OEM 866, а не UTF-8.
    /// Без неё русские названия (например схем питания) приходят как «кракозябры».
    /// </summary>
    internal static Encoding OemEncoding { get; } = CreateOemEncoding();

    private static Encoding CreateOemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Запускает консольную утилиту без окна и ждёт завершения. Вызывать не из UI-потока.
    /// <paramref name="outputEncoding"/> — кодировка вывода утилиты; null — по умолчанию (UTF-8).
    /// </summary>
    public static ProcessResult Run(string fileName, string arguments, int timeoutMs = 15_000, Encoding? outputEncoding = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (outputEncoding is not null)
        {
            startInfo.StandardOutputEncoding = outputEncoding;
            startInfo.StandardErrorEncoding = outputEncoding;
        }

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
