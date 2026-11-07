using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Tweaks;
using WinOptimizer.ViewModels;
using WinOptimizer.Views;

namespace WinOptimizer;

public partial class App : Application
{
    /// <summary>
    /// Тема тёмная, поэтому цвет текста по умолчанию (чёрный в системных стилях) заменяем на светлый:
    /// иначе любой элемент без явного Foreground получается «чёрным по чёрному».
    /// Вызывается до создания первого окна, пока метаданные свойств ещё можно переопределить.
    /// </summary>
    private static void ApplyLightTextDefaults()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5));
        brush.Freeze();
        // Каждый вызов отдельно: если WPF откажется переопределять метаданные, остальное всё равно сработает
        // (явные Foreground заданы у окон, карточек, полей ввода и подсказок в теме).
        foreach (var owner in new[] { typeof(TextBlock), typeof(Control) })
        {
            try
            {
                TextElement.ForegroundProperty.OverrideMetadata(owner, new FrameworkPropertyMetadata(brush));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Logger.Error($"Цвет текста по умолчанию для {owner.Name} не задан", ex);
            }
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyLightTextDefaults();
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            MessageBox.Show("WinOptimizer работает только в Windows 10 и новее.", "WinOptimizer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
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
        window.Show();
        _ = viewModel.CheckForUpdatesOnStartupAsync();
        Logger.Info("WinOptimizer запущен");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Необработанное исключение", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"Произошла непредвиденная ошибка:{Environment.NewLine}{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}" +
            $"Подробности записаны в {AppPaths.LogFile}. Приложение будет закрыто.",
            "WinOptimizer", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }
}
