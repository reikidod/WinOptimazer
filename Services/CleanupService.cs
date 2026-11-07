using System.Globalization;

namespace WinOptimizer.Services;

/// <param name="Files">Сколько файлов удалено (или было бы удалено при сканировании).</param>
/// <param name="Skipped">Сколько пропущено: свежие файлы, занятые программами, нет доступа, ссылки.</param>
/// <param name="Bytes">Объём освобождённого (или освободимого) места.</param>
public sealed record CleanupResult(int Files, int Skipped, long Bytes);

public interface ICleanupService
{
    /// <summary>Считает, сколько места можно освободить. Ничего не удаляет.</summary>
    CleanupResult Scan();

    /// <summary>Удаляет подходящие временные файлы.</summary>
    CleanupResult Clean();
}

/// <summary>
/// Очистка временных файлов. Консервативна по построению: трогает только файлы старше порога
/// (по умолчанию сутки, чтобы не ломать установщики и программы, которые прямо сейчас работают),
/// не заходит в ссылки (symlink/junction) и никогда не работает с корнем диска.
/// </summary>
public sealed class CleanupService : ICleanupService
{
    private readonly IReadOnlyList<string> _roots;
    private readonly TimeSpan _minAge;

    public CleanupService(IEnumerable<string> roots, TimeSpan? minAge = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var normalized = new List<string>();
        foreach (var root in roots)
        {
            var full = Path.GetFullPath(root);
            if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Корень диска нельзя использовать для очистки: {root}", nameof(roots));
            }

            // «C:\Temp» и «C:\Temp\» — одна папка: без нормализации файлы посчитались бы дважды.
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!normalized.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(full);
            }
        }

        _roots = normalized;
        _minAge = minAge ?? TimeSpan.FromHours(24);
    }

    /// <summary>Временные папки текущего пользователя и Windows.</summary>
    public static CleanupService ForWindowsTemp() => new(
    [
        Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
    ]);

    public CleanupResult Scan() => Run(delete: false);

    public CleanupResult Clean() => Run(delete: true);

    private CleanupResult Run(bool delete)
    {
        var files = 0;
        var skipped = 0;
        long bytes = 0;
        var cutoff = DateTime.UtcNow - _minAge;

        foreach (var root in _roots.Where(Directory.Exists))
        {
            Walk(new DirectoryInfo(root), isRoot: true);
        }

        return new CleanupResult(files, skipped, bytes);

        void Walk(DirectoryInfo directory, bool isRoot)
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                return;
            }

            foreach (var entry in entries)
            {
                // Ссылки не разворачиваем: иначе удаление пошло бы за пределы временной папки.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped++;
                    continue;
                }

                if (entry is DirectoryInfo subdirectory)
                {
                    Walk(subdirectory, isRoot: false);
                    continue;
                }

                if (entry is not FileInfo file)
                {
                    continue;
                }

                try
                {
                    if (file.LastWriteTimeUtc > cutoff)
                    {
                        skipped++;
                        continue;
                    }

                    var length = file.Length;
                    if (delete)
                    {
                        file.IsReadOnly = false;
                        file.Delete();
                    }

                    files++;
                    bytes += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Файл занят процессом или нет прав: это нормальная ситуация для временных папок.
                    skipped++;
                }
            }

            if (delete && !isRoot)
            {
                try
                {
                    if (!directory.EnumerateFileSystemInfos().Any())
                    {
                        directory.Delete();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Папка занята — оставляем.
                }
            }
        }
    }
}

public static class ByteSize
{
    private static readonly string[] Units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];

    public static string Format(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 Б";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : "0.#";
        return string.Create(CultureInfo.CurrentCulture, $"{value.ToString(format, CultureInfo.CurrentCulture)} {Units[unit]}");
    }
}
