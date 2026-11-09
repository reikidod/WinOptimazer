namespace WinOptimizer.Infrastructure;

/// <summary>Пути данных приложения (логи, бэкапы, кэш). Рядом с exe или в LocalAppData.</summary>
public static class AppPaths
{
    public static string Root { get; } = InitRoot();
    public static string DataDirectory => Path.Combine(Root, "Data");
    public static string LogFile => Path.Combine(DataDirectory, "app.log");
    public static string BackupFile => Path.Combine(DataDirectory, "tweaks-backup.json");
    public static string DownloadsDirectory => Path.Combine(DataDirectory, "downloads");

    private static string InitRoot()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                return baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { /* fallback */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinOptimizer");
    }
}

/// <summary>Простой файловый лог.</summary>
public static class Logger
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                RotateIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex is not null) line += Environment.NewLine + ex;
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch { /* лог не должен ронять приложение */ }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(AppPaths.LogFile);
            if (info.Exists && info.Length > 2 * 1024 * 1024)
                File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", overwrite: true);
        }
        catch { /* ignore */ }
    }
}
