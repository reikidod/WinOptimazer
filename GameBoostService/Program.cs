using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WinOptimizer.GameBoostService;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--install", StringComparison.OrdinalIgnoreCase))
            return ServiceInstaller.Install();
        if (args.Length > 0 && args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            return ServiceInstaller.Uninstall();

        Host.CreateDefaultBuilder(args)
            .UseWindowsService(options => options.ServiceName = ServiceInstaller.ServiceName)
            .ConfigureServices(services => services.AddHostedService<GameBoostWorker>())
            .Build()
            .Run();
        return 0;
    }
}

internal static class ServiceInstaller
{
    public const string ServiceName = "WinOptimizerGameBoost";
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "GameBoost");
    private static readonly string ServiceDirectory = Path.Combine(DataDirectory, "Service");
    private static readonly string LogPath = Path.Combine(DataDirectory, "install.log");
    private static string Executable => Path.Combine(ServiceDirectory, "WinOptimizer.GameBoostService.exe");
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorElevationRequired = 10;

    // Restore a predictable service DACL for this application-owned service.
    // Administrators and LocalSystem can manage/start it; standard users can only query it.
    private const string DefaultServiceSecurityDescriptor =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;AU)(A;;CCLCSWRPWPDTLOCRRC;;;PU)";

    public static int Install()
    {
        if (!IsAdministrator())
            return Fail("Установка службы требует подтверждённого запуска от имени администратора.", ErrorElevationRequired);

        var query = RunSc("query");
        var exists = query.ExitCode == 0;
        if (!exists && query.ExitCode != ErrorServiceDoesNotExist)
            return Fail("Не удалось проверить существование службы.", query);

        if (exists)
        {
            var stop = RunSc("stop");
            if (stop.ExitCode != 0 && stop.ExitCode != ErrorServiceNotActive)
                return Fail("Не удалось остановить существующую службу перед обновлением.", stop);
            if (!WaitForState(1))
                return Fail("Служба не перешла в состояние STOPPED за отведённое время.", 2);
        }

        if (!CopyAndProtectPayload())
            return Fail("Не удалось скопировать файлы службы или настроить права доступа. См. install.log.", 6);

        var serviceArguments = new[]
        {
            // Keep the full executable path quoted in the SCM ImagePath value.
            "binPath=", $"\"{Executable}\"",
            "start=", "auto",
            "obj=", "LocalSystem",
            "DisplayName=", "WinOptimizer Game Boost"
        };
        var configured = exists
            ? RunSc("config", serviceArguments)
            : RunSc("create", serviceArguments);
        if (configured.ExitCode != 0)
            return Fail(exists ? "Не удалось обновить конфигурацию службы." : "Не удалось зарегистрировать службу в Windows.", configured);

        var security = RunSc("sdset", DefaultServiceSecurityDescriptor);
        if (security.ExitCode != 0)
            return Fail("Не удалось восстановить права управления игровой службой. Проверьте install.log.", security);

        LogOptionalCommand("description", RunSc("description",
            "Автоматическое повышение производительности игр с восстановлением настроек"));
        LogOptionalCommand("failure actions", RunSc("failure",
            "reset=", "0", "actions=", "restart/5000/restart/5000/restart/5000"));
        LogOptionalCommand("failure flag", RunSc("failureflag", "1"));

        var start = RunSc("start");
        if (start.ExitCode != 0 && start.ExitCode != ErrorServiceAlreadyRunning)
        {
            DiagnoseServiceStartFailure();
            return Fail("Диспетчер служб Windows отказал в запуске WinOptimizer Game Boost. Это не обязательно связано с UAC; подробности конфигурации и ACL записаны в install.log.", start);
        }

        if (!WaitForState(4))
            return Fail("Команда запуска завершилась, но служба не перешла в состояние RUNNING. Проверьте service.log.", 7);

        WriteLog("Установка/обновление службы успешно завершены; состояние RUNNING подтверждено.");
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsAdministrator())
            return Fail("Удаление службы требует подтверждённого запуска от имени администратора.", ErrorElevationRequired);

        var query = RunSc("query");
        if (query.ExitCode == ErrorServiceDoesNotExist)
            return 0;
        if (query.ExitCode != 0)
            return Fail("Не удалось проверить существование службы перед удалением.", query);

        var stop = RunSc("stop");
        if (stop.ExitCode != 0 && stop.ExitCode != ErrorServiceNotActive)
            return Fail("Не удалось остановить службу перед удалением.", stop);
        if (!WaitForState(1))
            return Fail("Служба не остановилась; удаление отменено, чтобы не повредить работающий сервис.", 5);

        var delete = RunSc("delete");
        if (delete.ExitCode != 0)
            return Fail("Windows не смогла удалить регистрацию службы.", delete);

        try
        {
            if (Directory.Exists(ServiceDirectory))
                Directory.Delete(ServiceDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteLog("Служба удалена, но каталог файлов не удалось очистить: " + ex.Message);
        }

        WriteLog("Служба успешно удалена.");
        return 0;
    }

    private static void DiagnoseServiceStartFailure()
    {
        var configuration = RunSc("qc");
        var security = RunSc("sdshow");
        var directoryAcl = RunIcacls(ServiceDirectory);
        var executableAcl = RunIcacls(Executable);

        WriteLog("Диагностика отказа запуска службы:");
        WriteLog($"sc qc: exit={configuration.ExitCode}; {configuration.Output} {configuration.Error}");
        WriteLog($"sc sdshow: exit={security.ExitCode}; {security.Output} {security.Error}");
        WriteLog($"icacls service directory: exit={directoryAcl.ExitCode}; {directoryAcl.Output} {directoryAcl.Error}");
        WriteLog($"icacls service executable: exit={executableAcl.ExitCode}; {executableAcl.Output} {executableAcl.Error}");
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            WriteLog("Не удалось проверить административные права: " + ex);
            return false;
        }
    }

    private static bool CopyAndProtectPayload()
    {
        try
        {
            var sourceDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var sourceExecutable = Path.Combine(sourceDirectory, "WinOptimizer.GameBoostService.exe");
            if (!File.Exists(sourceExecutable))
            {
                WriteLog("Файл службы отсутствует в пакете: " + sourceExecutable);
                return false;
            }

            Directory.CreateDirectory(ServiceDirectory);
            var copiedFiles = 0;
            foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var destination = Path.Combine(ServiceDirectory, Path.GetFileName(source));
                if (File.Exists(destination))
                {
                    try
                    {
                        var attributes = File.GetAttributes(destination);
                        if ((attributes & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        WriteLog($"Не удалось снять атрибут ReadOnly с {destination}: {ex.Message}");
                        throw;
                    }
                }

                File.Copy(source, destination, overwrite: true);
                copiedFiles++;
            }

            if (copiedFiles == 0 || !File.Exists(Executable))
            {
                WriteLog($"Копирование payload не завершено: файлов={copiedFiles}; ожидаемый файл={Executable}");
                return false;
            }

            var acl = RunIcacls(
                ServiceDirectory, "/inheritance:r", "/grant:r",
                "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "/T", "/C");
            if (acl.ExitCode != 0)
            {
                WriteLog($"Первичная настройка ACL завершилась с кодом {acl.ExitCode}: {acl.Output} {acl.Error}");

                // Повторная попытка без флагов наследования для систем, где icacls
                // не принимает комбинацию /inheritance:r и object/container inheritance.
                var fallback = RunIcacls(
                    ServiceDirectory, "/grant:r",
                    "*S-1-5-18:F", "*S-1-5-32-544:F", "/T", "/C");
                if (fallback.ExitCode != 0)
                {
                    WriteLog($"Повторная настройка ACL завершилась с кодом {fallback.ExitCode}: {fallback.Output} {fallback.Error}");
                    return false;
                }
            }

            WriteLog($"Payload службы подготовлен: скопировано файлов={copiedFiles}; ACL подтверждены.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Ошибка подготовки файлов службы: " + ex);
            return false;
        }
    }

    private static (int ExitCode, string Output, string Error) RunIcacls(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
            return (-1, "", "Не удалось запустить icacls.exe.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "icacls.exe превысил лимит времени.");
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static bool WaitForState(int expectedState)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var query = RunSc("query");
            if (query.ExitCode != 0)
                return false;
            if (System.Text.RegularExpressions.Regex.IsMatch(
                query.Output, $@"STATE\s*:\s*{expectedState}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static ScResult RunSc(string command, params string[] arguments)
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
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add(ServiceName);
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new ScResult(-1, "", "Не удалось создать процесс sc.exe");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new ScResult(-1, "", "Превышено время ожидания sc.exe");
            }

            var result = new ScResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            WriteLog($"sc.exe {command}: exit={result.ExitCode}; stdout={result.Output}; stderr={result.Error}");
            return result;
        }
        catch (Exception ex)
        {
            WriteLog($"Не удалось выполнить sc.exe {command}: {ex}");
            return new ScResult(-1, "", ex.Message);
        }
    }

    private static void LogOptionalCommand(string name, ScResult result)
    {
        if (result.ExitCode != 0)
            WriteLog($"Не удалось настроить {name}: exit={result.ExitCode}; {result.Error} {result.Output}");
    }

    private static int Fail(string message, ScResult result) =>
        Fail($"{message} sc.exe exit={result.ExitCode}; {result.Error} {result.Output}", result.ExitCode == 0 ? 1 : result.ExitCode);

    private static int Fail(string message, int exitCode)
    {
        WriteLog("ERROR: " + message);
        return exitCode is > 0 and <= 255 ? exitCode : 1;
    }

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch { /* Ошибка журнала не должна маскировать исходную ошибку установки. */ }
    }

    private sealed record ScResult(int ExitCode, string Output, string Error);
}
