using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public enum ScheduledTaskState
{
    Unknown = 0,
    Disabled = 1,
    Queued = 2,
    Ready = 3,
    Running = 4,
}

public sealed record ScheduledTaskEntry(
    string Name,
    string Path,
    bool Enabled,
    int State,
    DateTime? LastRunTime,
    DateTime? NextRunTime,
    int LastTaskResult)
{
    public string StateLabel => State switch
    {
        (int)ScheduledTaskState.Disabled => "Отключена",
        (int)ScheduledTaskState.Queued => "В очереди",
        (int)ScheduledTaskState.Ready => Enabled ? "Готова" : "Отключена",
        (int)ScheduledTaskState.Running => "Выполняется",
        _ => Enabled ? "Состояние неизвестно" : "Отключена",
    };

    public string LastRunText => FormatTime(LastRunTime);
    public string NextRunText => FormatTime(NextRunTime);

    private static string FormatTime(DateTime? value) =>
        value is null ? "Нет данных" : value.Value.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
}

public interface ITaskSchedulerService
{
    IReadOnlyList<ScheduledTaskEntry> GetTasks();
}

/// <summary>
/// Read-only доступ к Windows Task Scheduler через штатный COM API.
/// Сервис только читает определения и состояние задач — он не запускает,
/// не останавливает, не включает и не отключает задания.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTaskSchedulerService : ITaskSchedulerService
{
    private const int IncludeHiddenTasks = 1;

    public IReadOnlyList<ScheduledTaskEntry> GetTasks()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Планировщик задач доступен только в Windows.");

        var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
            ?? throw new InvalidOperationException("Компонент планировщика задач Windows недоступен.");

        object serviceObject = Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Не удалось создать подключение к планировщику задач.");

        object? rootFolder = null;
        try
        {
            dynamic service = serviceObject;
            service.Connect(null, null, null, null);
            rootFolder = service.GetFolder("\\");
            var result = new List<ScheduledTaskEntry>();
            ReadFolder(rootFolder, result);
            return result
                .OrderBy(task => task.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (COMException ex)
        {
            Logger.Error("Не удалось прочитать задачи планировщика Windows", ex);
            throw new InvalidOperationException("Не удалось получить список задач планировщика Windows. Проверьте доступность службы планировщика.", ex);
        }
        finally
        {
            ReleaseComObject(rootFolder);
            ReleaseComObject(serviceObject);
        }
    }

    private static void ReadFolder(object folderObject, ICollection<ScheduledTaskEntry> result)
    {
        dynamic folder = folderObject;
        object? tasksObject = null;
        object? foldersObject = null;

        try
        {
            tasksObject = folder.GetTasks(IncludeHiddenTasks);
            dynamic tasks = tasksObject;
            var taskCount = Convert.ToInt32(tasks.Count, CultureInfo.InvariantCulture);

            for (var index = 1; index <= taskCount; index++)
            {
                object? taskObject = null;
                try
                {
                    taskObject = tasks.Item(index);
                    dynamic task = taskObject;
                    result.Add(new ScheduledTaskEntry(
                        Convert.ToString(task.Name, CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToString(task.Path, CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToBoolean(task.Enabled, CultureInfo.InvariantCulture),
                        Convert.ToInt32(task.State, CultureInfo.InvariantCulture),
                        ReadTaskTime(task.LastRunTime),
                        ReadTaskTime(task.NextRunTime),
                        Convert.ToInt32(task.LastTaskResult, CultureInfo.InvariantCulture)));
                }
                catch (COMException ex)
                {
                    Logger.Error("Не удалось прочитать отдельную задачу планировщика", ex);
                }
                finally
                {
                    ReleaseComObject(taskObject);
                }
            }

            foldersObject = folder.GetFolders(0);
            dynamic folders = foldersObject;
            var folderCount = Convert.ToInt32(folders.Count, CultureInfo.InvariantCulture);
            for (var index = 1; index <= folderCount; index++)
            {
                object? childFolder = null;
                try
                {
                    childFolder = folders.Item(index);
                    ReadFolder(childFolder, result);
                }
                catch (COMException ex)
                {
                    Logger.Error("Не удалось прочитать папку планировщика Windows", ex);
                }
                finally
                {
                    ReleaseComObject(childFolder);
                }
            }
        }
        finally
        {
            ReleaseComObject(foldersObject);
            ReleaseComObject(tasksObject);
        }
    }

    private static DateTime? ReadTaskTime(object? value)
    {
        if (value is DateTime dateTime && dateTime.Year >= 1900)
            return dateTime;

        return null;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }
    }
}

public static class ScheduledTaskFilter
{
    public static bool Matches(ScheduledTaskEntry task, string? searchText, string? statusFilter)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            // Search terms may span folder boundaries, e.g. "windows update"
            // should match "\Microsoft\Windows\UpdateOrchestrator\...".
            var searchableText = $"{task.Name} {task.Path}"
                .Replace('\\', ' ')
                .Replace('/', ' ');
            var terms = searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (terms.Any(term => !searchableText.Contains(term, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return statusFilter switch
        {
            "Включены" => task.Enabled,
            "Отключены" => !task.Enabled,
            "Выполняются" => task.State == (int)ScheduledTaskState.Running,
            "Готовы" => task.State == (int)ScheduledTaskState.Ready && task.Enabled,
            _ => true,
        };
    }
}
