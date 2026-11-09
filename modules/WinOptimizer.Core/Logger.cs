using System.Globalization;

namespace WinOptimizer.Infrastructure;

public static class AppPaths
{
    /// <summary>Каталог данных приложения: бэкапы значений реестра и лог.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinOptimizer");

    public static string BackupFile => Path.Combine(DataDirectory, "backup.json");

    public static string LogFile => Path.Combine(DataDirectory, "log.txt");

    public static string DownloadsDirectory => Path.Combine(DataDirectory, "downloads");
}

/// <summary>Простой файловый лог. Сбой записи лога никогда не должен ломать приложение.</summary>
public static class Logger
{
    private const long MaxLogBytes = 1_000_000;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                RotateIfNeeded();
                var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}");
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Лог недоступен: молча продолжаем.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(AppPaths.LogFile);
        if (info.Exists && info.Length > MaxLogBytes)
        {
            File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", overwrite: true);
        }
    }
}
