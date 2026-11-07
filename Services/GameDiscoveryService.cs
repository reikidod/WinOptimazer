using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public sealed record DiscoveredGame(
    string Id,
    string Name,
    string ExePath,
    string InstallDir,
    string Platform);

public interface IGameDiscoveryService
{
    Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct = default);
}

/// <summary>
/// Поиск установленных игр (в духе OptiScaler Client): Steam, Epic, GOG, Xbox Games.
/// </summary>
public sealed class GameDiscoveryService : IGameDiscoveryService
{
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "unitycrashhandler64", "unitycrashhandler32", "crashreportclient",
        "ue4prereqsetup", "vc_redist", "dxsetup", "dotnet", "uninstall",
        "redist", "setup", "installer", "launcher_steam", "steamerrorreporter",
        "cefsharp", "notification_helper", "vcredist", "easyanticheat",
        "battleye", "pbsvc", "werfault",
    };

    public Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct = default)
        => Task.Run(() => ScanCore(ct), ct);

    private IReadOnlyList<DiscoveredGame> ScanCore(CancellationToken ct)
    {
        var list = new List<DiscoveredGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try { ScanSteam(list, seen, ct); }
        catch (Exception ex) { Logger.Error("Steam scan", ex); }

        try { ScanEpic(list, seen, ct); }
        catch (Exception ex) { Logger.Error("Epic scan", ex); }

        try { ScanGog(list, seen, ct); }
        catch (Exception ex) { Logger.Error("GOG scan", ex); }

        try { ScanXbox(list, seen, ct); }
        catch (Exception ex) { Logger.Error("Xbox scan", ex); }

        try { ScanCommonFolders(list, seen, ct); }
        catch (Exception ex) { Logger.Error("Folder scan", ex); }

        return list
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void ScanSteam(List<DiscoveredGame> list, HashSet<string> seen, CancellationToken ct)
    {
        var steamRoot = FindSteamRoot();
        if (steamRoot is null) return;

        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        var libraries = new List<string> { Path.Combine(steamRoot, "steamapps") };
        if (File.Exists(vdf))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
                var p = m.Groups[1].Value.Replace(@"\\", @"\");
                var apps = Path.Combine(p, "steamapps");
                if (Directory.Exists(apps)) libraries.Add(apps);
            }
        }

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var manifest in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifest);
                    var name = MatchVdf(text, "name");
                    var dirName = MatchVdf(text, "installdir");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dirName)) continue;
                    if (name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Proton", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var gameDir = Path.Combine(lib, "common", dirName);
                    if (!Directory.Exists(gameDir)) continue;
                    var exe = FindBestExe(gameDir);
                    if (exe is null) continue;
                    Add(list, seen, name, exe, gameDir, "Steam");
                }
                catch { /* skip broken manifest */ }
            }
        }
    }

    private static string? FindSteamRoot()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = k?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p.Replace('/', '\\');
        }
        catch { }

        foreach (var c in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam",
                     @"D:\Steam", @"E:\Steam",
                 })
        {
            if (Directory.Exists(c)) return c;
        }
        return null;
    }

    private static string? MatchVdf(string text, string key)
    {
        var m = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static void ScanEpic(List<DiscoveredGame> list, HashSet<string> seen, CancellationToken ct)
    {
        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) return;

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = File.ReadAllText(file);
                var name = JsonString(json, "DisplayName") ?? JsonString(json, "AppName");
                var dir = JsonString(json, "InstallLocation");
                var exeRel = JsonString(json, "LaunchExecutable");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                if (name.Contains("Unreal Engine", StringComparison.OrdinalIgnoreCase)) continue;

                string? exe = null;
                if (!string.IsNullOrEmpty(exeRel))
                {
                    var candidate = Path.Combine(dir, exeRel.Replace('/', '\\'));
                    if (File.Exists(candidate)) exe = candidate;
                }
                exe ??= FindBestExe(dir);
                if (exe is null) continue;
                Add(list, seen, name, exe, dir, "Epic");
            }
            catch { }
        }
    }

    private static void ScanGog(List<DiscoveredGame> list, HashSet<string> seen, CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var games = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (games is null) return;
            foreach (var id in games.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var g = games.OpenSubKey(id);
                if (g is null) continue;
                var name = g.GetValue("gameName") as string ?? g.GetValue("name") as string;
                var path = g.GetValue("path") as string;
                var exeName = g.GetValue("exe") as string;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                string? exe = null;
                if (!string.IsNullOrEmpty(exeName))
                {
                    var c = Path.IsPathRooted(exeName) ? exeName : Path.Combine(path, exeName);
                    if (File.Exists(c)) exe = c;
                }
                exe ??= FindBestExe(path);
                if (exe is null) continue;
                Add(list, seen, name, exe, path, "GOG");
            }
        }
        catch { }
    }

    private static void ScanXbox(List<DiscoveredGame> list, HashSet<string> seen, CancellationToken ct)
    {
        foreach (var root in new[] { @"C:\XboxGames", @"D:\XboxGames", @"E:\XboxGames" })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                var content = Path.Combine(dir, "Content");
                var search = Directory.Exists(content) ? content : dir;
                var exe = FindBestExe(search);
                if (exe is null) continue;
                Add(list, seen, Path.GetFileName(dir), exe, search, "Xbox");
            }
        }
    }

    private static void ScanCommonFolders(List<DiscoveredGame> list, HashSet<string> seen, CancellationToken ct)
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files", "Epic Games"));
        }
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                // не дублировать глубокий скан Steam/Epic
                if (dir.Contains("steamapps", StringComparison.OrdinalIgnoreCase)) continue;
                var exe = FindBestExe(dir, maxDepth: 2);
                if (exe is null) continue;
                Add(list, seen, Path.GetFileName(dir), exe, dir, "Папка");
            }
        }
    }

    private static string? FindBestExe(string root, int maxDepth = 3)
    {
        try
        {
            // UE: Binaries\Win64\*Shipping*.exe
            foreach (var binaries in Directory.EnumerateDirectories(root, "Binaries", SearchOption.AllDirectories).Take(8))
            {
                foreach (var win in new[] { "Win64", "WinGDK", "Win64Shipping" })
                {
                    var folder = Path.Combine(binaries, win);
                    if (!Directory.Exists(folder)) continue;
                    var shipping = Directory.EnumerateFiles(folder, "*Shipping*.exe").FirstOrDefault();
                    if (shipping is not null) return shipping;
                    var any = Directory.EnumerateFiles(folder, "*.exe")
                        .Where(IsLikelyGameExe)
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .FirstOrDefault();
                    if (any is not null) return any;
                }
            }

            // Cyberpunk-style bin\x64
            foreach (var bin in new[] { @"bin\x64", @"bin\x64_dx12", "binaries", "Retail", "Binaries" })
            {
                var folder = Path.Combine(root, bin);
                if (!Directory.Exists(folder)) continue;
                var exe = Directory.EnumerateFiles(folder, "*.exe")
                    .Where(IsLikelyGameExe)
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();
                if (exe is not null) return exe;
            }

            return Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
                .Where(f => DepthFrom(root, f) <= maxDepth)
                .Where(IsLikelyGameExe)
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static int DepthFrom(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length - 1;
    }

    private static bool IsLikelyGameExe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (SkipNames.Contains(name)) return false;
        if (name.Contains("crash", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("redist", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith("Setup", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith("Installer", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (new FileInfo(path).Length < 1_000_000) return false; // < 1MB unlikely
        }
        catch { return false; }
        return true;
    }

    private static void Add(List<DiscoveredGame> list, HashSet<string> seen, string name, string exe, string dir, string platform)
    {
        if (!seen.Add(exe)) return;
        list.Add(new DiscoveredGame($"{platform}:{exe}".GetHashCode().ToString("X"), name.Trim(), exe, dir, platform));
    }

    private static string? JsonString(string json, string key)
    {
        var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
        if (!m.Success) return null;
        return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\/", "/");
    }
}
