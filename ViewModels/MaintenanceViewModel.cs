using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

/// <summary>Страница «Обслуживание»: очистка временных файлов, автозагрузка, восстановление системы.</summary>
public sealed class MaintenanceViewModel : ObservableObject
{
    private readonly ICleanupService _cleanup;
    private readonly IStartupService _startup;
    private readonly IDialogService _dialogs;

    private bool _isBusy;
    private string _cleanupSummary = "Ещё не проверялось";
    private string? _startupError;

    public MaintenanceViewModel(ICleanupService cleanup, IStartupService startup, IDialogService dialogs)
    {
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy, ReportUnexpected);
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => !IsBusy, ReportUnexpected);
        RefreshStartupCommand = new AsyncRelayCommand(LoadStartupAsync, () => !IsBusy, ReportUnexpected);
        OpenRestoreCommand = new RelayCommand(OpenSystemRestore);
    }

    public ObservableCollection<StartupItemViewModel> StartupItems { get; } = new();

    public ICommand ScanCommand { get; }

    public ICommand CleanCommand { get; }

    public ICommand RefreshStartupCommand { get; }

    public ICommand OpenRestoreCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string CleanupSummary
    {
        get => _cleanupSummary;
        private set => SetField(ref _cleanupSummary, value);
    }

    public string? StartupError
    {
        get => _startupError;
        private set
        {
            if (SetField(ref _startupError, value))
            {
                OnPropertyChanged(nameof(HasStartupError));
                OnPropertyChanged(nameof(HasNoStartupItems));
            }
        }
    }

    public bool HasStartupError => !string.IsNullOrEmpty(_startupError);

    public bool HasNoStartupItems => StartupItems.Count == 0 && !HasStartupError;

    public async Task InitializeAsync()
    {
        // Ошибки здесь не критичны: страница просто покажет «не удалось», остальное приложение работает.
        await ScanAsync();
        await LoadStartupAsync();
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            CleanupSummary = "Считаю…";
            var result = await Task.Run(_cleanup.Scan);
            CleanupSummary = result.Files == 0
                ? "Нечего очищать: подходящих временных файлов нет"
                : string.Create(CultureInfo.CurrentCulture, $"Можно освободить {ByteSize.Format(result.Bytes)} ({result.Files} файлов)");
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            Logger.Error("Не удалось просканировать временные файлы", ex);
            CleanupSummary = "Не удалось посчитать: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CleanAsync()
    {
        var choice = _dialogs.Ask(
            "Очистка временных файлов",
            $"Будут удалены временные файлы старше 24 часов из папок Temp пользователя и Windows.{Environment.NewLine}" +
            "Файлы, которые сейчас используются программами, пропускаются.",
            primary: "Очистить");
        if (choice != DialogChoice.Primary)
        {
            return;
        }

        IsBusy = true;
        try
        {
            CleanupSummary = "Очищаю…";
            var result = await Task.Run(_cleanup.Clean);
            Logger.Info($"Очистка temp: удалено {result.Files}, освобождено {result.Bytes} байт, пропущено {result.Skipped}");
            CleanupSummary = string.Create(
                CultureInfo.CurrentCulture,
                $"Освобождено {ByteSize.Format(result.Bytes)} (удалено файлов: {result.Files}, пропущено: {result.Skipped})");
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            Logger.Error("Не удалось очистить временные файлы", ex);
            CleanupSummary = "Очистка не выполнена: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadStartupAsync()
    {
        IsBusy = true;
        try
        {
            var entries = await Task.Run(_startup.GetEntries);
            StartupItems.Clear();
            foreach (var entry in entries)
            {
                StartupItems.Add(new StartupItemViewModel(entry, _startup));
            }

            StartupError = null;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            Logger.Error("Не удалось прочитать автозагрузку", ex);
            StartupError = "Не удалось прочитать список автозагрузки: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasNoStartupItems));
        }
    }

    private void OpenSystemRestore()
    {
        try
        {
            // Полный путь, а не имя: приложение работает с правами администратора.
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rstrui.exe")) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Logger.Error("Не удалось открыть «Восстановление системы»", ex);
            _dialogs.Info("Не удалось открыть", ex.Message);
        }
    }

    private static bool IsExpectedFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or ArgumentException;

    private void ReportUnexpected(Exception ex) =>
        _dialogs.Info("Непредвиденная ошибка", $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Подробности записаны в {AppPaths.LogFile}");
}
