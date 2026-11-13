using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
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
        RunSc("stop");
        RunSc("delete");
        var binPath = $"\\"{Executable}\\"";
        var create = RunSc("create", $"binPath= {binPath}", "start= auto", "obj= LocalSystem", "DisplayName= WinOptimizer Game Boost");
        if (create != 0) return create;
        RunSc("description", "WinOptimizerGameBoost", "Автоматическое повышение производительности игр с восстановлением настроек");
        return RunSc("start");
    }

    public static int Uninstall()
    {
        RunSc("stop");
        return RunSc("delete");
    }

    private static int RunSc(params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            Arguments = string.Join(" ", new[] { ServiceName }.Concat(args.Select(Quote))),
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit(15000);
        return process?.ExitCode ?? 1;
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\\"{value}\\"" : value;
}