using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Runtime.InteropServices;
using System.Windows.Data;
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
    private readonly ITaskSchedulerService _taskScheduler;

    private bool _isBusy;
    private string _cleanupSummary = "Ещё не проверялось";
    private string? _startupError;
    private string? _taskSchedulerError;
    private string _taskSearchText = string.Empty;
    private string _taskStatusFilter = "Все";

    public MaintenanceViewModel(ICleanupService cleanup, IStartupService startup, IDialogService dialogs, ITaskSchedulerService? taskScheduler = null)
    {
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _taskScheduler = taskScheduler ?? new WindowsTaskSchedulerService();
        FilteredScheduledTasks = CollectionViewSource.GetDefaultView(ScheduledTasks);
        FilteredScheduledTasks.Filter = item =>
            item is ScheduledTaskEntry task && ScheduledTaskFilter.Matches(task, TaskSearchText, TaskStatusFilter);

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy, ReportUnexpected);
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => !IsBusy, ReportUnexpected);
        RefreshStartupCommand = new AsyncRelayCommand(LoadStartupAsync, () => !IsBusy, ReportUnexpected);
        RefreshScheduledTasksCommand = new AsyncRelayCommand(LoadScheduledTasksAsync, () => !IsBusy, ReportUnexpected);
        ToggleScheduledTaskCommand = new AsyncRelayCommand<ScheduledTaskEntry>(ToggleScheduledTaskAsync,
            task => !IsBusy && task?.CanChangeEnabledState == true, ReportUnexpected);
        OpenRestoreCommand = new RelayCommand(OpenSystemRestore);
    }

    public ObservableCollection<StartupItemViewModel> StartupItems { get; } = new();

    public ObservableCollection<ScheduledTaskEntry> ScheduledTasks { get; } = new();

    public ICollectionView FilteredScheduledTasks { get; }

    public string[] TaskFilterOptions { get; } = ["Все", "Включены", "Отключены", "Выполняются", "Готовы", "Системные", "Драйверы", "Пользовательские"];

    public ICommand ScanCommand { get; }

    public ICommand CleanCommand { get; }

    public ICommand RefreshStartupCommand { get; }

    public ICommand RefreshScheduledTasksCommand { get; }

    public ICommand ToggleScheduledTaskCommand { get; }

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

    public string? TaskSchedulerError
    {
        get => _taskSchedulerError;
        private set
        {
            if (SetField(ref _taskSchedulerError, value))
            {
                OnPropertyChanged(nameof(HasTaskSchedulerError));
                OnPropertyChanged(nameof(HasNoScheduledTasks));
            }
        }
    }

    public bool HasTaskSchedulerError => !string.IsNullOrEmpty(TaskSchedulerError);

    public bool HasNoScheduledTasks => ScheduledTasks.Count == 0 && !HasTaskSchedulerError;

    public string ScheduledTasksSummary => $"Показано задач: {FilteredScheduledTasks.Cast<object>().Count()} из {ScheduledTasks.Count}";

    public string TaskSearchText
    {
        get => _taskSearchText;
        set
        {
            if (SetField(ref _taskSearchText, value ?? string.Empty))
            {
                FilteredScheduledTasks.Refresh();
                OnPropertyChanged(nameof(ScheduledTasksSummary));
            }
        }
    }

    public string TaskStatusFilter
    {
        get => _taskStatusFilter;
        set
        {
            if (SetField(ref _taskStatusFilter, value ?? "Все"))
            {
                FilteredScheduledTasks.Refresh();
                OnPropertyChanged(nameof(ScheduledTasksSummary));
            }
        }
    }

    public async Task InitializeAsync()
    {
        // Ошибки здесь не критичны: страница просто покажет «не удалось», остальное приложение работает.
        await ScanAsync();
        await LoadStartupAsync();
        await LoadScheduledTasksAsync();
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
            try
            {
                new OperationHistoryService().Record("Обслуживание", "Очистка временных файлов",
                    $"Удалено: {result.Files}; освобождено: {ByteSize.Format(result.Bytes)}; пропущено: {result.Skipped}");
            }
            catch (Exception historyError) when (historyError is IOException or UnauthorizedAccessException)
            {
                Logger.Error("Не удалось записать историю очистки", historyError);
            }
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

    private async Task LoadScheduledTasksAsync()
    {
        IsBusy = true;
        try
        {
            var tasks = await Task.Run(_taskScheduler.GetTasks);
            ScheduledTasks.Clear();
            foreach (var task in tasks)
                ScheduledTasks.Add(task);

            TaskSchedulerError = null;
            FilteredScheduledTasks.Refresh();
            OnPropertyChanged(nameof(ScheduledTasksSummary));
            OnPropertyChanged(nameof(HasNoScheduledTasks));
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            Logger.Error("Не удалось прочитать задачи планировщика Windows", ex);
            TaskSchedulerError = "Не удалось прочитать задачи планировщика: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleScheduledTaskAsync(ScheduledTaskEntry? task)
    {
        if (task is null || !task.CanChangeEnabledState) return;

        if (_taskScheduler is not ITaskSchedulerManagementService management)
        {
            _dialogs.Info("Управление недоступно", "Подключённый источник планировщика поддерживает только чтение.");
            return;
        }

        var enable = !task.Enabled;
        var action = enable ? "включить" : "отключить";
        var actionTitle = enable ? "Включить задачу" : "Отключить задачу";
        var message = enable
            ? $"Задача «{task.Name}» снова сможет запускаться по своему расписанию. Изменение можно отменить, отключив задачу обратно.\n\n{task.Path}"
            : $"Будущие автоматические запуски задачи «{task.Name}» будут отключены. Уже выполняющийся экземпляр не остановится. Изменение можно отменить, включив задачу обратно.\n\n{task.Path}";

        var choice = _dialogs.Ask(actionTitle, message, primary: enable ? "Включить" : "Отключить");
        if (choice != DialogChoice.Primary) return;

        IsBusy = true;
        try
        {
            await Task.Run(() => management.SetEnabled(task.Path, enable));

            try
            {
                new OperationHistoryService().Record(
                    "Планировщик задач",
                    $"{(enable ? "Включена" : "Отключена")}: {task.Path}",
                    enable ? "Включена" : "Отключена");
            }
            catch (Exception historyError) when (historyError is IOException or UnauthorizedAccessException)
            {
                Logger.Error("Не удалось записать изменение задачи в историю операций", historyError);
            }

            try
            {
                var refreshed = await Task.Run(_taskScheduler.GetTasks);
                ScheduledTasks.Clear();
                foreach (var item in refreshed)
                    ScheduledTasks.Add(item);

                TaskSchedulerError = null;
                FilteredScheduledTasks.Refresh();
                OnPropertyChanged(nameof(ScheduledTasksSummary));
                OnPropertyChanged(nameof(HasNoScheduledTasks));
            }
            catch (Exception refreshError) when (IsExpectedFailure(refreshError))
            {
                Logger.Error("Состояние задачи изменено, но не удалось обновить список планировщика", refreshError);
                TaskSchedulerError = $"Состояние задачи изменено, но список не удалось обновить: {refreshError.Message}";
                _dialogs.Info("Задача изменена", "Состояние задачи изменено. Нажмите «Обновить», чтобы перечитать список.");
            }
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            Logger.Error($"Не удалось {action} задачу планировщика {task.Path}", ex);
            TaskSchedulerError = $"Не удалось {action} задачу «{task.Name}»: {ex.Message}";
            _dialogs.Info("Не удалось изменить задачу", ex.Message);
        }
        finally
        {
            IsBusy = false;
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
        ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or ArgumentException or COMException;

    private void ReportUnexpected(Exception ex) =>
        _dialogs.Info("Непредвиденная ошибка", $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Подробности записаны в {AppPaths.LogFile}");
}
