using System.Diagnostics;
using System.ComponentModel;
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
    private static readonly string ServiceDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "GameBoost", "Service");
    private static string Executable => Path.Combine(ServiceDirectory, "WinOptimizer.GameBoostService.exe");

    public static int Install()
    {
        var exists = RunSc("query") == 0;
        if (exists)
        {
            RunSc("stop");
            if (!WaitForStopped()) return 2;
        }

        if (!CopyAndProtectPayload()) return 6;

        var serviceArguments = new[]
        {
            $"binPath= \"{Executable}\"",
            "start= auto",
            "obj= LocalSystem",
            "DisplayName= \"WinOptimizer Game Boost\""
        };
        var configured = exists
            ? RunSc("config", serviceArguments)
            : RunSc("create", serviceArguments);
        if (configured != 0) return 3;

        RunSc("description", "\"Автоматическое повышение производительности игр с восстановлением настроек\"");
        RunSc("failure", "reset= 0", "actions= restart/5000/restart/5000/restart/5000");
        RunSc("failureflag", "1");
        return RunSc("start");
    }

    public static int Uninstall()
    {
        if (RunSc("query") != 0) return 0;
        RunSc("stop");
        if (!WaitForStopped()) return 5;
        var result = RunSc("delete");
        if (result == 0)
        {
            try { if (Directory.Exists(ServiceDirectory)) Directory.Delete(ServiceDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private static bool CopyAndProtectPayload()
    {
        try
        {
            var sourceDirectory = AppContext.BaseDirectory;
            var sourceExecutable = Path.Combine(sourceDirectory, "WinOptimizer.GameBoostService.exe");
            if (!File.Exists(sourceExecutable)) return false;

            Directory.CreateDirectory(ServiceDirectory);
            foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
                File.Copy(source, Path.Combine(ServiceDirectory, Path.GetFileName(source)), overwrite: true);

            using var acl = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
                ArgumentList =
                {
                    ServiceDirectory,
                    "/inheritance:r",
                    "/grant:r",
                    "*S-1-5-18:(OI)(CI)F",
                    "*S-1-5-32-544:(OI)(CI)F",
                    "/T",
                    "/C"
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (acl is null) return false;
            acl.WaitForExit(15000);
            return acl.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool WaitForStopped()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                    Arguments = $"query {ServiceName}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                });
                var output = process?.StandardOutput.ReadToEnd() ?? "";
                process?.WaitForExit(3000);
                if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return true;
                if (process?.ExitCode != 0) return false;
            }
            catch { return false; }
            Thread.Sleep(500);
        }
        return false;
    }

    private static int RunSc(string command, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            Arguments = string.Join(" ", new[] { command, ServiceName }.Concat(arguments)),
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit(15000);
        return process?.ExitCode ?? 1;
    }

}