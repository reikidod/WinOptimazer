using System.Windows;
using System.Windows.Threading;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Tweaks;
using WinOptimizer.ViewModels;
using WinOptimizer.Views;

namespace WinOptimizer;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Необработанная ошибка в фоновой задаче", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("Критическая ошибка", args.ExceptionObject as Exception);

        // Два окна одновременно портят общий файл бэкапа твиков и мешают обновлению.
        _instanceMutex = new Mutex(initiallyOwned: true, @"Global\WinOptimizer.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("WinOptimizer уже запущен.", "WinOptimizer", MessageBoxButton.OK, MessageBoxImage.Information);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown(0);
            return;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            MessageBox.Show("WinOptimizer работает только в Windows 10 и новее.", "WinOptimizer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        // Тема и акцент применяются до создания окон, чтобы не мигать цветами по умолчанию.
        ThemeManager.Initialize();

        // Первое окно в WPF становится MainWindow. При ShutdownMode=OnMainWindowClose
        // закрытие приветствия сразу гасило бы всё приложение — временно отключаем.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            if (AppSettings.Current.ShowWelcome)
            {
                var welcome = new WelcomeWindow();
                welcome.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Ошибка приветственного окна", ex);
        }

        var dialogs = new DialogService();
        var updates = new UpdateService();
        var backup = new BackupStore(AppPaths.BackupFile);
        var viewModel = new MainViewModel(
            TweakCatalog.Create(backup),
            new RestorePointService(),
            new SystemInfoService(),
            dialogs,
            updates,
            new MaintenanceViewModel(
                CleanupService.ForWindowsTemp(),
                new StartupService(new WindowsStartupStore()),
                dialogs),
            new DebloatViewModel(new DebloatService(), dialogs),
            new AppsViewModel(new AppInstallService(), new ZapretService(), new TgWsProxyService(), dialogs),
            new StoreViewModel(new MsStoreService(), dialogs),
            new GamesViewModel(new GameDiscoveryService(), new OptiScalerService(), dialogs));

        var window = new MainWindow(viewModel);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        if (AppSettings.Current.CheckUpdatesOnStartup)
            _ = viewModel.CheckForUpdatesOnStartupAsync();
        Logger.Info("WinOptimizer запущен");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Ползунки сохраняются с задержкой; при закрытии сразу после изменения записываем значения принудительно.
        try { AppSettings.Current.Save(); } catch (Exception ex) { Logger.Error("Сохранение настроек при выходе", ex); }
        try { PerformanceTelemetryService.DisposePhysicalDiskCounters(); }
        catch (Exception ex) { Logger.Error("Освобождение счётчиков дисков при выходе", ex); }
        try { GpuTelemetryService.Dispose(); }
        catch (Exception ex) { Logger.Error("Освобождение счётчиков GPU при выходе", ex); }
        try { _instanceMutex?.ReleaseMutex(); catch (ApplicationException) { /* уже освобождён */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private int _unhandledCount;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Необработанное исключение", e.Exception);
        e.Handled = true;

        // Одна ошибка в одном разделе не должна закрывать всё приложение вместе с несохранённым выбором.
        // Если ошибки идут подряд (больше пяти), состояние уже ненадёжно — закрываемся.
        var fatal = ++_unhandledCount > 5;
        MessageBox.Show(
            $"Произошла непредвиденная ошибка:{Environment.NewLine}{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}" +
            $"Подробности записаны в {AppPaths.LogFile}." +
            (fatal ? " Приложение будет закрыто." : " Можно продолжить работу."),
            "WinOptimizer", MessageBoxButton.OK, MessageBoxImage.Error);
        if (fatal) Shutdown(1);
    }
}
