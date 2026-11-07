using System.Diagnostics;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

/// <summary>
/// Поиск уже установленных программ (Zapret, TG WS Proxy) по всей системе: запомненный путь,
/// запущенные процессы, службы, автозагрузка, а затем обход папок пользователя и всех фиксированных дисков.
/// </summary>
public static class AppLocator
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    private static string CacheFile => Path.Combine(AppPaths.DataDirectory, "locations.json");

    // Папки, где заведомо нечего искать (системные, кэши, служебные).
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "WinSxS", "WindowsApps", "node_modules",
        ".git", ".nuget", "Recovery", "Config.Msi", "$WinREAgent", "Microsoft", "Packages", "Temp", "INetCache",
        "DriverStore", "Installer", "assembly",
    };

    // ---------- Запомненные пути ----------

    public static string? GetCached(string key)
    {
        lock (Gate)
        {
            LoadCache();
            return _cache!.TryGetValue(key, out var v) ? v : null;
        }
    }

    public static void Remember(string key, string path)
    {
        lock (Gate)
        {
            LoadCache();
            _cache![key] = path;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(_cache));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Error("Не удалось сохранить найденные пути", ex);
            }
        }
    }

    private static void LoadCache()
    {
        if (_cache is not null) return;
        try
        {
            _cache = File.Exists(CacheFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _cache = new();
        }
    }

    // ---------- Запущенные процессы, службы, автозагрузка ----------

    /// <summary>Полный путь к exe запущенного процесса (в том числе запущенного от администратора).</summary>
    public static string? GetProcessPath(Process p)
    {
        try
        {
            var path = p.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path)) return path;
        }
        catch
        {
            // нет доступа к процессу с повышенными правами — пробуем через WMI
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {p.Id}");
            foreach (var o in searcher.Get())
            {
                var path = o["ExecutablePath"] as string;
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch
        {
            // WMI недоступен
        }

        return null;
    }

    public static IEnumerable<string> GetProcessPaths(params string[] processNames)
    {
        foreach (var name in processNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                using (p)
                {
                    var path = GetProcessPath(p);
                    if (path != null) yield return path;
                }
            }
        }
    }

    /// <summary>Путь к exe из ImagePath службы (например, служба «zapret» → winws.exe).</summary>
    public static string? GetServiceExePath(string serviceName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return ExtractExePath(key?.GetValue("ImagePath") as string);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Пути из значений автозагрузки (Run) пользователя и компьютера, подходящие под шаблон имени.</summary>
    public static IEnumerable<string> GetAutorunPaths(Regex namePattern)
    {
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            Microsoft.Win32.RegistryKey? key = null;
            try { key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); }
            catch { /* нет доступа */ }

            using (key)
            {
                if (key is null) continue;
                foreach (var valueName in key.GetValueNames())
                {
                    var path = ExtractExePath(key.GetValue(valueName) as string);
                    if (path != null && namePattern.IsMatch(Path.GetFileName(path)))
                        yield return path;
                }
            }
        }
    }

    private static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        string path;
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) return null;
            path = command[1..end];
        }
        else
        {
            var idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            path = command[..(idx + 4)];
        }

        path = Environment.ExpandEnvironmentVariables(path);
        return File.Exists(path) ? path : null;
    }

    // ---------- Поиск по дискам ----------

    /// <summary>
    /// Ищет сразу несколько программ за один обход. <paramref name="probes"/>: ключ → функция, которая для папки
    /// возвращает путь к найденному (или null). Сначала быстро проверяются папки пользователя и Program Files,
    /// затем все фиксированные диски целиком.
    /// </summary>
    public static Dictionary<string, string> Search(
        IReadOnlyDictionary<string, Func<string, string?>> probes,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        Walk(PriorityRoots(), maxDepth: 6, TimeSpan.FromSeconds(30), "в папках пользователя и Program Files");
        if (found.Count < probes.Count)
            Walk(DriveRoots(), maxDepth: 14, TimeSpan.FromMinutes(3), "на дисках");

        return found;

        void Walk(IEnumerable<string> roots, int maxDepth, TimeSpan budget, string where)
        {
            var deadline = DateTime.UtcNow + budget;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scanned = 0;
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                // Скрытые и системные папки (AppData, ProgramData) нужны; не заходим только в ссылки/junction.
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                var queue = new Queue<(string Dir, int Depth)>();
                queue.Enqueue((root, 0));

                while (queue.Count > 0)
                {
                    if (ct.IsCancellationRequested || DateTime.UtcNow > deadline) return;
                    var (dir, depth) = queue.Dequeue();
                    if (!visited.Add(dir)) continue;

                    if (++scanned % 400 == 0)
                        progress?.Report($"Ищу {where}: {dir}");

                    foreach (var (key, probe) in probes)
                    {
                        if (found.ContainsKey(key)) continue;
                        string? hit = null;
                        try { hit = probe(dir); }
                        catch { /* папка недоступна */ }
                        if (hit != null)
                        {
                            found[key] = hit;
                            if (found.Count == probes.Count) return;
                        }
                    }

                    if (depth >= maxDepth) continue;
                    IEnumerable<string> subs;
                    try { subs = Directory.EnumerateDirectories(dir, "*", options).ToList(); }
                    catch { continue; }

                    foreach (var sub in subs)
                    {
                        if (SkipNames.Contains(Path.GetFileName(sub))) continue;
                        queue.Enqueue((sub, depth + 1));
                    }
                }
            }
        }
    }

    private static IEnumerable<string> PriorityRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            Path.Combine(profile, "Downloads"),
            Path.Combine(profile, "Desktop"),
            Path.Combine(profile, "Documents"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            profile,
        };
        return list.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> DriveRoots()
    {
        // Системный диск первым — именно там чаще всего лежат программы.
        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var drives = new List<string>();
        try
        {
            drives.AddRange(DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName));
        }
        catch (IOException)
        {
        }

        return drives.OrderByDescending(d => string.Equals(d, sysRoot, StringComparison.OrdinalIgnoreCase));
    }
}
