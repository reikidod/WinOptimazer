using System.Diagnostics;
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
    private static string Executable => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;

    public static int Install()
    {
        var exists = RunSc("query") == 0;
        if (exists)
        {
            RunSc("stop");
            if (!WaitForStopped()) return 2;
            if (RunSc("config", $"binPath= \"{Executable}\"", "start= auto", "obj= LocalSystem",
                    "DisplayName= \"WinOptimizer Game Boost\"") != 0)
                return 3;
        }
        else if (RunSc("create", $"binPath= \"{Executable}\"", "start= auto", "obj= LocalSystem",
                     "DisplayName= \"WinOptimizer Game Boost\"") != 0)
        {
            return 4;
        }

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
        return RunSc("delete");
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

    private static int RunSc(params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            Arguments = string.Join(" ", new[] { args[0], ServiceName }.Concat(args.Skip(1))),
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit(15000);
        return process?.ExitCode ?? 1;
    }
}