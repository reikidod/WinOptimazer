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
    int LastTaskResult,
    string? Author = null,
    string? Description = null)
{
    public string StateLabel => State switch
    {
        (int)ScheduledTaskState.Disabled => "Отключена",
        (int)ScheduledTaskState.Queued => "В очереди",
        (int)ScheduledTaskState.Ready => Enabled ? "Готова" : "Отключена",
        (int)ScheduledTaskState.Running => "Выполняется",
        _ => Enabled ? "Состояние неизвестно" : "Отключена",
    };

    public string PublisherLabel => string.IsNullOrWhiteSpace(Author) ? "Издатель не указан" : Author;

    public string CategoryLabel
    {
        get
        {
            var metadata = Path + " " + Author;
            if (ContainsAny(metadata, "nvidia", "amd", "advanced micro devices", "intel", "realtek", "qualcomm", "driver"))
                return "Драйвер";

            if (Path.StartsWith("\\Microsoft\\Windows\\", StringComparison.OrdinalIgnoreCase) ||
                (Author?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false))
                return "Системная";

            return "Пользовательская";
        }
    }

    public string LastRunText => FormatTime(LastRunTime);
    public string NextRunText => FormatTime(NextRunTime);
    public string LastTaskResultText => string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", unchecked((uint)LastTaskResult));
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool CanChangeEnabledState => ScheduledTaskManagementPolicy.CanChange(this);
    public string EnabledActionLabel => Enabled ? "Отключить" : "Включить";

    private static bool ContainsAny(string source, params string[] values) =>
        values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string FormatTime(DateTime? value) =>
        value is null ? "Нет данных" : value.Value.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
}

public interface ITaskSchedulerService
{
    IReadOnlyList<ScheduledTaskEntry> GetTasks();
}

/// <summary>Обратимое управление только пользовательскими задачами планировщика.</summary>
public interface ITaskSchedulerManagementService
{
    void SetEnabled(string taskPath, bool enabled);
}

/// <summary>Не разрешает менять системные, драйверные и некорректно адресованные задачи.</summary>
public static class ScheduledTaskManagementPolicy
{
    public static bool CanChange(ScheduledTaskEntry? task)
    {
        if (task is null || string.IsNullOrWhiteSpace(task.Path)) return false;

        var path = task.Path.Trim();
        if (!path.StartsWith(@"\", StringComparison.Ordinal) ||
            path.Any(char.IsControl) ||
            path.Split('\\').Any(segment => segment is "." or ".."))
            return false;

        // Microsoft-задачи блокируются по всему дереву, не только в \Windows.
        if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase))
            return false;

        return task.CategoryLabel == "Пользовательская";
    }
}

/// <summary>
/// Доступ к Windows Task Scheduler через штатный COM API.
/// Чтение охватывает все задачи; изменение состояния ограничено пользовательскими задачами.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTaskSchedulerService : ITaskSchedulerService, ITaskSchedulerManagementService
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

    /// <summary>
    /// Включает или отключает только пользовательскую задачу.
    /// Уже выполняющийся экземпляр не останавливается; состояние можно вернуть обратным действием.
    /// </summary>
    public void SetEnabled(string taskPath, bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Планировщик задач доступен только в Windows.");

        if (string.IsNullOrWhiteSpace(taskPath))
            throw new ArgumentException("Путь задачи обязателен.", nameof(taskPath));

        if (!taskPath.StartsWith(@"\", StringComparison.Ordinal) ||
            taskPath.Any(char.IsControl) ||
            taskPath.Split('\\').Any(segment => segment is "." or ".."))
            throw new ArgumentException("Укажите полный корректный путь задачи планировщика.", nameof(taskPath));

        var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
            ?? throw new InvalidOperationException("Компонент планировщика задач Windows недоступен.");

        object serviceObject = Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Не удалось создать подключение к планировщику.");

        object? taskObject = null;
        object? definitionObject = null;
        object? registrationInfoObject = null;
        try
        {
            dynamic service = serviceObject;
            service.Connect(null, null, null, null);
            taskObject = service.GetTask(taskPath);
            dynamic task = taskObject;

            var actualPath = Convert.ToString(task.Path, CultureInfo.InvariantCulture) ?? string.Empty;
            if (!string.Equals(actualPath, taskPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Планировщик вернул задачу с другим путём. Изменение отменено.");

            var name = Convert.ToString(task.Name, CultureInfo.InvariantCulture) ?? string.Empty;
            var wasEnabled = Convert.ToBoolean(task.Enabled, CultureInfo.InvariantCulture);
            var state = Convert.ToInt32(task.State, CultureInfo.InvariantCulture);
            string? author = null;

            try
            {
                definitionObject = task.Definition;
                dynamic definition = definitionObject;
                registrationInfoObject = definition.RegistrationInfo;
                dynamic registrationInfo = registrationInfoObject;
                author = Convert.ToString(registrationInfo.Author, CultureInfo.InvariantCulture);
            }
            catch (COMException ex)
            {
                Logger.Error("Не удалось проверить издателя задачи перед изменением", ex);
                throw new InvalidOperationException("Не удалось проверить издателя задачи. Изменение запрещено.", ex);
            }

            var entry = new ScheduledTaskEntry(
                name, actualPath, wasEnabled, state, null, null, 0, author);
            if (!ScheduledTaskManagementPolicy.CanChange(entry))
                throw new UnauthorizedAccessException("Изменять можно только пользовательские задачи. Системные задачи Microsoft и задачи драйверов доступны только для просмотра.");

            if (wasEnabled == enabled) return;

            task.Enabled = enabled;
            var actualEnabled = Convert.ToBoolean(task.Enabled, CultureInfo.InvariantCulture);
            if (actualEnabled != enabled)
                throw new InvalidOperationException("Планировщик не подтвердил изменение состояния задачи.");

            Logger.Info($"Задача планировщика {(enabled ? "включена" : "отключена")}: {actualPath}");
        }
        catch (COMException ex)
        {
            Logger.Error("Не удалось изменить состояние задачи планировщика Windows", ex);
            throw new InvalidOperationException("Не удалось изменить задачу. Возможно, недостаточно прав или задача защищена Windows.", ex);
        }
        finally
        {
            ReleaseComObject(registrationInfoObject);
            ReleaseComObject(definitionObject);
            ReleaseComObject(taskObject);
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
                object? definitionObject = null;
                object? registrationInfoObject = null;
                try
                {
                    taskObject = tasks.Item(index);
                    dynamic task = taskObject;
                    string? author = null;
                    string? description = null;

                    try
                    {
                        definitionObject = task.Definition;
                        dynamic definition = definitionObject;
                        registrationInfoObject = definition.RegistrationInfo;
                        dynamic registrationInfo = registrationInfoObject;
                        author = Convert.ToString(registrationInfo.Author, CultureInfo.InvariantCulture);
                        description = Convert.ToString(registrationInfo.Description, CultureInfo.InvariantCulture);
                    }
                    catch (COMException ex)
                    {
                        Logger.Error("Не удалось прочитать издателя задачи планировщика", ex);
                    }

                    result.Add(new ScheduledTaskEntry(
                        Convert.ToString(task.Name, CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToString(task.Path, CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToBoolean(task.Enabled, CultureInfo.InvariantCulture),
                        Convert.ToInt32(task.State, CultureInfo.InvariantCulture),
                        ReadTaskTime(task.LastRunTime),
                        ReadTaskTime(task.NextRunTime),
                        Convert.ToInt32(task.LastTaskResult, CultureInfo.InvariantCulture),
                        author,
                        description));
                }
                catch (COMException ex)
                {
                    Logger.Error("Не удалось прочитать отдельную задачу планировщика", ex);
                }
                finally
                {
                    ReleaseComObject(registrationInfoObject);
                    ReleaseComObject(definitionObject);
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
            var searchableText = task.Name + " " + task.Path + " " + task.Author + " " + task.Description;
            searchableText = searchableText.Replace('\\', ' ').Replace('/', ' ');
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
            "Системные" => task.CategoryLabel == "Системная",
            "Драйверы" => task.CategoryLabel == "Драйвер",
            "Пользовательские" => task.CategoryLabel == "Пользовательская",
            _ => true,
        };
    }
}
