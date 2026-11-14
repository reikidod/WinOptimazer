using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WinOptimizer.Services;

public sealed record GameBoostStatus(bool IsInstalled, bool IsRunning, bool IsActive, string Message, int ConfiguredGames);

/// <summary>Безопасный IPC-клиент службы Game Boost. Служба принимает только status/configure.</summary>
public sealed class GameBoostClient
{
    private const string PipeName = "WinOptimizer.GameBoost.v1";
    private readonly string _serviceExecutable;

    public GameBoostClient()
    {
        _serviceExecutable = Path.Combine(AppContext.BaseDirectory, "GameBoostService", "WinOptimizer.GameBoostService.exe");
    }

    public async Task<(bool Success, string Message)> InstallAsync()
    {
        if (!File.Exists(_serviceExecutable))
            return (false, "Файл службы не найден. Переустановите WinOptimizer из полного установочного пакета.");
        return await RunElevatedAsync("--install");
    }

    public Task<(bool Success, string Message)> UninstallAsync() => RunElevatedAsync("--uninstall");

    public async Task<(bool Success, string Message, bool Active, int Count)> ConfigureAsync(IEnumerable<string> paths)
    {
        var response = await SendAsync(new PipeRequest("configure", paths.ToArray()));
        return response is null ? (false, "Служба не отвечает. Проверьте её состояние.", false, 0)
            : (response.Success, response.Message, response.IsActive, response.ConfiguredGames.Length);
    }

    public async Task<GameBoostStatus> GetStatusAsync()
    {
        var state = await QueryServiceStateAsync();
        if (!state.Installed)
            return new(false, false, false, "Служба не установлена", 0);
        if (!state.Running)
            return new(true, false, false, "Служба установлена, но остановлена. Её можно восстановить кнопкой запуска.", 0);

        var response = await SendAsync(new PipeRequest("status"));
        return response is null
            ? new(true, false, false, "Служба запущена, но IPC-канал не отвечает. Проверьте журнал службы.", 0)
            : new(true, true, response.IsActive, response.Message, response.ConfiguredGames.Length);
    }

    private static async Task<(bool Installed, bool Running)> QueryServiceStateAsync()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("query");
            startInfo.ArgumentList.Add("WinOptimizerGameBoost");

            using var process = Process.Start(startInfo);
            if (process is null)
                return (false, false);

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, false);
            }

            var output = await outputTask;
            _ = await errorTask;
            var installed = output.Contains("SERVICE_NAME: WinOptimizerGameBoost", StringComparison.OrdinalIgnoreCase);
            return (installed, installed && System.Text.RegularExpressions.Regex.IsMatch(
                output, @"STATE\s*:\s*4\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (false, false);
        }
    }

    private async Task<(bool Success, string Message)> RunElevatedAsync(string argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(_serviceExecutable, argument)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(_serviceExecutable)!
            });
            if (process is null)
                return (false, "Не удалось запустить установщик службы.");

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "Установка службы превысила 45 секунд. Проверьте журнал установки.");
            }

            if (process.ExitCode == 0)
                return (true, "Служба успешно установлена и её состояние RUNNING подтверждено.");

            var details = ReadInstallerLogTail();
            var reason = process.ExitCode switch
            {
                10 => "Установщику не удалось подтвердить права администратора. Повторите установку и разрешите запрос UAC.",
                5 => "Диспетчер служб Windows отказал в запуске игровой службы (Access denied). Это не обязательно ошибка UAC; проверьте диагностику службы в install.log.",
                _ => $"Установщик службы завершился с кодом {process.ExitCode}."
            };
            return (false, string.IsNullOrWhiteSpace(details) ? reason : reason + Environment.NewLine + details);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Установка отменена в окне контроля учётных записей Windows.");
        }
        catch (Exception ex)
        {
            return (false, "Не удалось запустить установщик службы: " + ex.Message);
        }
    }

    private static string ReadInstallerLogTail()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WinOptimizer", "GameBoost", "install.log");
            if (!File.Exists(path)) return "";
            return string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(8));
        }
        catch { return ""; }
    }

    private static async Task<PipeResponse?> SendAsync(PipeRequest request)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1500);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(request));
                var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
                return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<PipeResponse>(line);
            }
            catch
            {
                if (attempt < 2) await Task.Delay(400);
            }
        }
        return null;
    }

    private sealed record PipeRequest(string Command, string[]? Paths = null);
    private sealed record PipeResponse(bool Success, string Message, string[] ConfiguredGames, bool IsActive);
}
