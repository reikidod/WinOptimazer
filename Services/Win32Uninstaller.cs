using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

/// <summary>Остаток после удаления: папка, файл (ярлык) или раздел реестра.</summary>
public sealed record Leftover(string Kind, string Path)
{
    public override string ToString() => Kind switch
    {
        "Registry" when Path.StartsWith("UNINSTALL:", StringComparison.Ordinal) =>
            "Реестр (запись в списке программ): " + Path["UNINSTALL:".Length..],
        "Registry" => "Реестр: " + Path,
        "File" => "Файл: " + Path,
        _ => "Папка: " + Path,
    };
}

/// <summary>
/// Классические (Win32) программы: список из реестра, удаление штатным деинсталлятором
/// и поиск/удаление остатков (папки, ярлыки, ключи реестра) в стиле Geek Uninstaller.
/// </summary>
public static class Win32Uninstaller
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // Папки и ключи реестра, которые никогда не трогаем.
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Windows NT", "Common Files", "Internet Explorer", "WindowsApps", "WindowsPowerShell",
        "Packages", "Temp", "Programs", "ModifiableWindowsApps", "Classes", "Policies", "Wow6432Node", "Clients",
        "RegisteredApplications", "Microsoft Games", "Reference Assemblies", "dotnet", "Windows Defender",
        "Windows Mail", "Windows Media Player", "Windows Photo Viewer", "Windows Security", "Intel", "NVIDIA Corporation",
        "NVIDIA", "AMD", "Realtek", "Package Cache", "USOShared", "regid.1991-06.com.microsoft", "SystemApps",
        "Default", "Public", "All Users", "Desktop", "Documents", "Downloads",
    };

    private static readonly string[] LauncherExecutables =
    {
        "steam.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "UbisoftConnect.exe", "upc.exe",
        "EADesktop.exe", "Origin.exe", "Battle.net.exe", "Battle.net Launcher.exe", "RiotClientServices.exe",
    };

    // ---------- Список ----------

    public static List<DebloatApp> List()
    {
        var result = new Dictionary<string, DebloatApp>(StringComparer.OrdinalIgnoreCase);
        var sources = new (RegistryHive Hive, RegistryView View, string Prefix)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
        };

        foreach (var (hive, view, prefix) in sources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(UninstallPath);
                if (root is null) continue;

                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var key = root.OpenSubKey(subName);
                        var app = ReadApp(key, subName, prefix);
                        if (app is null) continue;
                        result.TryAdd($"{app.DisplayName}|{app.Version}", app);
                    }
                    catch
                    {
                        // повреждённая запись — пропускаем
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Чтение списка программ: " + prefix, ex);
            }
        }

        return result.Values.ToList();
    }

    private static DebloatApp? ReadApp(RegistryKey? key, string subName, string prefix)
    {
        if (key is null) return null;
        var name = (key.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        if (Convert.ToInt32(key.GetValue("SystemComponent") ?? 0) == 1) return null;
        if (!string.IsNullOrEmpty(key.GetValue("ParentKeyName") as string)) return null;
        var release = key.GetValue("ReleaseType") as string;
        if (release is "Update" or "Hotfix" or "Security Update" or "Update Rollup") return null;
        if (Regex.IsMatch(name, @"^(KB\d+|(Security )?Update for|Hotfix for)", RegexOptions.IgnoreCase)) return null;
        if (name.Contains("WinOptimizer", StringComparison.OrdinalIgnoreCase)) return null;

        var uninstall = (key.GetValue("UninstallString") as string)?.Trim();
        var quiet = (key.GetValue("QuietUninstallString") as string)?.Trim();
        if (string.IsNullOrEmpty(uninstall) && string.IsNullOrEmpty(quiet)) return null;

        var publisher = (key.GetValue("Publisher") as string)?.Trim() ?? "";
        var version = (key.GetValue("DisplayVersion") as string)?.Trim() ?? "";
        var location = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');
        var launcher = DetectLauncher(subName, uninstall, quiet, location, out var launcherGameId);
        // Для игр из лаунчеров путь по деинсталлятору — это папка самого лаунчера, её угадывать нельзя.
        if (launcher is null && (string.IsNullOrEmpty(location) || !Directory.Exists(location)))
            location = GuessLocation(key, uninstall);
        if (launcher != null && !string.IsNullOrEmpty(location) && !Directory.Exists(location))
            location = null;
        var sizeKb = 0L;
        try { sizeKb = Convert.ToInt64(key.GetValue("EstimatedSize") ?? 0); } catch { /* не число */ }

        var parts = new List<string>();
        if (launcher != null) parts.Add($"Игра {launcher} · удаляется через {launcher}");
        if (publisher.Length > 0) parts.Add(publisher);
        if (version.Length > 0) parts.Add("версия " + version);
        if (sizeKb > 0) parts.Add(FormatSize(sizeKb));

        var isMsi = Convert.ToInt32(key.GetValue("WindowsInstaller") ?? 0) == 1;
        return new DebloatApp(
            Id: $"{prefix}\\{subName}",
            DisplayName: name,
            PackageName: subName,
            PackageFullName: "",
            Description: parts.Count > 0 ? string.Join(" · ", parts) : "Установленная программа",
            IsInstalled: true,
            IsRecommended: false,
            InstallLocation: location,
            IconPath: ResolveIconPath(key, uninstall, location),
            Kind: AppKind.Win32,
            Publisher: publisher,
            Version: version,
            UninstallCommand: uninstall,
            QuietUninstallCommand: quiet,
            RegistryKeyPath: $"{prefix}\\{subName}",
            SizeKb: sizeKb,
            IsMsi: isMsi,
            Launcher: launcher,
            LauncherGameId: launcherGameId);
    }

    // ---------- Игры из лаунчеров ----------

    private static bool Has(string? text, string needle) =>
        !string.IsNullOrEmpty(text) && text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Определяет игру, которой управляет лаунчер (Steam, Epic, GOG, Ubisoft, EA, Battle.net, Riot).
    /// Такие игры нельзя удалять «снаружи»: их команда удаления указывает на лаунчер, и принудительное
    /// удаление или очистка остатков стёрли бы папку самого лаунчера.
    /// </summary>
    internal static string? DetectLauncher(string subName, string? uninstall, string? quiet, string? location, out string? gameId)
    {
        gameId = null;
        var cmd = (uninstall ?? "") + " " + (quiet ?? "");

        var m = Regex.Match(subName, @"^Steam App (\d+)$", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(cmd, @"steam://uninstall/(\d+)", RegexOptions.IgnoreCase);
        if (m.Success) { gameId = m.Groups[1].Value; return "Steam"; }
        if (Has(cmd, "steam://") || Has(location, @"\steamapps\common\")) return "Steam";

        if (Has(cmd, "com.epicgames.launcher://") ||
            (Has(location, @"\Epic Games\") && !Has(location, @"\Epic Games\Launcher"))) return "Epic Games";
        if (Has(location, @"\GOG Galaxy\Games\") || Has(cmd, "goggalaxy://")) return "GOG Galaxy";
        if (Has(cmd, "uplay://") || Has(cmd, "upc://") || Has(location, @"\Ubisoft Game Launcher\games\")) return "Ubisoft Connect";
        if (Has(cmd, "origin2://") || Has(cmd, "EADesktop") ||
            Has(location, @"\EA Games\") || Has(location, @"\Origin Games\")) return "EA app";
        if (Has(cmd, "Battle.net") && Has(cmd, "--uninstall")) return "Battle.net";
        if (Has(cmd, "RiotClientServices")) return "Riot Client";
        return null;
    }

    /// <summary>Открывает лаунчер на удалении игры; саму игру удаляет лаунчер, и только её.</summary>
    public static (bool Ok, string Message) UninstallViaLauncher(DebloatApp app)
    {
        var (url, name) = app.Launcher switch
        {
            "Steam" => (app.LauncherGameId is { Length: > 0 }
                ? $"steam://uninstall/{app.LauncherGameId}" : "steam://open/games", "Steam"),
            "Epic Games" => ("com.epicgames.launcher://apps/library", "Epic Games Launcher"),
            "GOG Galaxy" => ("goggalaxy://", "GOG Galaxy"),
            "Ubisoft Connect" => ("uplay://", "Ubisoft Connect"),
            "EA app" => ("origin2://library/open", "EA app"),
            "Battle.net" => ("battlenet://", "Battle.net"),
            _ => ((string?)null, app.Launcher ?? "лаунчер"),
        };

        if (url is null)
            return (true, $"Откройте {name} и удалите игру там — так не пострадает сам лаунчер.");

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Logger.Info($"Удаление игры {app.DisplayName} передано лаунчеру: {url}");
            return (true, app.Launcher == "Steam" && app.LauncherGameId != null
                ? "Открыт Steam — подтвердите удаление игры в его окне."
                : $"Открыт {name} — удалите игру в библиотеке.");
        }
        catch (Exception ex)
        {
            Logger.Error("Запуск лаунчера: " + app.DisplayName, ex);
            return (true, $"Не удалось открыть {name}. Откройте его вручную и удалите игру там.");
        }
    }

    private static string FormatSize(long kb) =>
        kb >= 1024 * 1024 ? $"{kb / 1024.0 / 1024.0:0.#} ГБ" : kb >= 1024 ? $"{kb / 1024.0:0} МБ" : $"{kb} КБ";

    private static string? GuessLocation(RegistryKey key, string? uninstall)
    {
        // Папка с деинсталлятором (кроме msiexec и системных каталогов) обычно и есть папка программы.
        var exe = ExtractExe(uninstall);
        if (exe is null) return null;
        var dir = Path.GetDirectoryName(exe);
        return dir is not null && IsSafeDirectory(dir) ? dir : null;
    }

    private static string? ResolveIconPath(RegistryKey key, string? uninstall, string? location)
    {
        var icon = (key.GetValue("DisplayIcon") as string)?.Trim();
        if (!string.IsNullOrEmpty(icon))
        {
            icon = icon.Trim('"');
            var comma = icon.LastIndexOf(',');
            if (comma > 1 && int.TryParse(icon[(comma + 1)..], out _)) icon = icon[..comma];
            icon = Environment.ExpandEnvironmentVariables(icon.Trim().Trim('"'));
            if (File.Exists(icon)) return icon;
        }

        var exe = ExtractExe(uninstall);
        if (exe != null && !Path.GetFileName(exe).Contains("msiexec", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(exe))
            return exe;
        return null;
    }

    private static string? ExtractExe(string? command)
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

    // ---------- Удаление деинсталлятором ----------

    public static async Task<(bool Ok, string Message)> UninstallAsync(DebloatApp app, CancellationToken ct)
    {
        if (app.Launcher != null)
            return UninstallViaLauncher(app);
        var command = BuildCommand(app);
        if (command is null)
            return (false, "Для этой программы нет команды удаления.");

        try
        {
            Logger.Info($"Удаление {app.DisplayName}: {command}");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{command}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return (false, "Не удалось запустить деинсталлятор.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            // Некоторые деинсталляторы (Inno Setup и др.) запускают рабочую копию и сразу завершаются:
            // ждём, пока запись о программе исчезнет из реестра.
            for (var i = 0; i < 45; i++)
            {
                if (!KeyExists(app.RegistryKeyPath))
                    return (true, "Удалено");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            return (false, "Деинсталлятор завершился, но программа осталась в списке (возможно, удаление отменено).");
        }
        catch (OperationCanceledException)
        {
            return (false, "Удаление прервано по времени.");
        }
        catch (Exception ex)
        {
            Logger.Error("Деинсталлятор: " + app.DisplayName, ex);
            return (false, ex.Message);
        }
    }

    private static string? BuildCommand(DebloatApp app)
    {
        var uninstall = app.UninstallCommand;

        // MSI: msiexec /x {GUID} /qn
        var msi = Regex.Match(uninstall ?? "", @"(?i)msiexec(?:\.exe)?\s+/[IX]\s*(\{[0-9A-F\-]+\})");
        if (msi.Success)
            return $"msiexec.exe /x {msi.Groups[1].Value} /qn /norestart";
        if (app.IsMsi && Regex.IsMatch(app.PackageName, @"^\{[0-9A-Fa-f\-]+\}$"))
            return $"msiexec.exe /x {app.PackageName} /qn /norestart";

        if (!string.IsNullOrWhiteSpace(app.QuietUninstallCommand))
            return app.QuietUninstallCommand;

        if (string.IsNullOrWhiteSpace(uninstall)) return null;

        // Inno Setup (unins000.exe) умеет тихое удаление
        var exe = ExtractExe(uninstall);
        if (exe != null && Regex.IsMatch(Path.GetFileName(exe), @"^unins\d*\.exe$", RegexOptions.IgnoreCase)
            && !uninstall.Contains("/SILENT", StringComparison.OrdinalIgnoreCase))
            return uninstall + " /VERYSILENT /NORESTART /SUPPRESSMSGBOXES";

        return uninstall; // остальное — штатное окно деинсталлятора
    }

    /// <summary>Принудительное удаление: закрывает процессы из папки программы, стирает папку и запись в реестре.</summary>
    public static (bool Ok, string Message) ForceRemove(DebloatApp app)
    {
        if (app.Launcher != null)
            return (false, $"Игру нужно удалять через {app.Launcher}.");
        try
        {
            var dir = app.InstallLocation;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                if (!IsSafeDirectory(dir))
                    return (false, "Папка программы выглядит системной, принудительное удаление отменено: " + dir);

                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        var path = AppLocator.GetProcessPath(p);
                        if (path != null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                            p.Kill(entireProcessTree: true);
                    }
                    catch { /* процесс уже завершился или недоступен */ }
                    finally { p.Dispose(); }
                }

                Thread.Sleep(500);
                Directory.Delete(dir, true);
            }

            DeleteRegistryKey(app.RegistryKeyPath is null ? null : "UNINSTALL:" + app.RegistryKeyPath);
            Logger.Info("Принудительно удалено: " + app.DisplayName);
            return (true, "Удалено принудительно");
        }
        catch (Exception ex)
        {
            Logger.Error("Принудительное удаление: " + app.DisplayName, ex);
            return (false, ex.Message);
        }
    }

    // ---------- Остатки ----------

    public static List<Leftover> FindLeftovers(DebloatApp app)
    {
        var found = new List<Leftover>();
        // Остатки игр лаунчера не ищем: лаунчер сам знает, что стирать, а чужая очистка может зацепить его папку.
        if (app.Launcher != null) return found;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(Leftover l)
        {
            if (seen.Add(l.Kind + "|" + l.Path)) found.Add(l);
        }

        // Пакет Store: данные приложения лежат в %LocalAppData%\Packages\<семейство>
        if (app.Kind == AppKind.Store)
        {
            if (!string.IsNullOrEmpty(app.PackageFamilyName))
            {
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", app.PackageFamilyName);
                if (Directory.Exists(data)) Add(new Leftover("Folder", data));
            }

            return found;
        }

        // Запись в реестре осталась без рабочего деинсталлятора
        if (app.RegistryKeyPath != null && KeyExists(app.RegistryKeyPath))
            Add(new Leftover("Registry", "UNINSTALL:" + app.RegistryKeyPath));

        if (!string.IsNullOrEmpty(app.InstallLocation) && Directory.Exists(app.InstallLocation)
            && IsSafeDirectory(app.InstallLocation))
            Add(new Leftover("Folder", app.InstallLocation));

        var names = CandidateNames(app);
        if (names.Count == 0) return found;

        foreach (var root in FolderRoots())
        {
            foreach (var dir in SafeEnumerateDirectories(root))
            {
                var leaf = Path.GetFileName(dir);
                if (ProtectedNames.Contains(leaf)) continue;
                if (names.Contains(Normalize(leaf)) && IsSafeDirectory(dir))
                    Add(new Leftover("Folder", dir));
            }
        }

        // Ярлыки в меню «Пуск» и на рабочем столе
        foreach (var root in ShortcutRoots())
        {
            foreach (var file in SafeEnumerateFiles(root, "*.lnk"))
            {
                if (names.Contains(Normalize(Path.GetFileNameWithoutExtension(file))))
                    Add(new Leftover("File", file));
            }

            foreach (var dir in SafeEnumerateDirectories(root))
            {
                var leaf = Path.GetFileName(dir);
                if (!ProtectedNames.Contains(leaf) && names.Contains(Normalize(leaf)))
                    Add(new Leftover("Folder", dir));
            }
        }

        // Разделы реестра: Software\<Имя> и Software\<Издатель>\<Имя>
        foreach (var (hive, view, prefix) in new[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64"),
                     (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32"),
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var software = baseKey.OpenSubKey("SOFTWARE");
                if (software is null) continue;

                foreach (var sub in software.GetSubKeyNames())
                {
                    if (ProtectedNames.Contains(sub)) continue;
                    if (names.Contains(Normalize(sub)))
                    {
                        Add(new Leftover("Registry", $"{prefix}\\SOFTWARE\\{sub}"));
                        continue;
                    }

                    if (string.IsNullOrEmpty(app.Publisher) ||
                        Normalize(sub) != Normalize(app.Publisher)) continue;
                    using var pub = software.OpenSubKey(sub);
                    if (pub is null) continue;
                    foreach (var inner in pub.GetSubKeyNames())
                        if (names.Contains(Normalize(inner)))
                            Add(new Leftover("Registry", $"{prefix}\\SOFTWARE\\{sub}\\{inner}"));
                }
            }
            catch
            {
                // нет доступа к ветке
            }
        }

        return found;
    }

    public static (int Removed, List<string> Failed) RemoveLeftovers(IEnumerable<Leftover> items)
    {
        var removed = 0;
        var failed = new List<string>();
        foreach (var item in items)
        {
            try
            {
                switch (item.Kind)
                {
                    case "Folder":
                        if (!IsSafeDirectory(item.Path)) { failed.Add(item.Path + " (защищённая папка)"); continue; }
                        if (Directory.Exists(item.Path)) Directory.Delete(item.Path, true);
                        break;
                    case "File":
                        if (File.Exists(item.Path)) File.Delete(item.Path);
                        break;
                    case "Registry":
                        DeleteRegistryKey(item.Path);
                        break;
                }

                removed++;
            }
            catch (Exception ex)
            {
                failed.Add($"{item.Path} ({ex.Message})");
            }
        }

        if (removed > 0) Logger.Info($"Удалено остатков: {removed}");
        foreach (var f in failed) Logger.Error("Не удалось удалить остаток: " + f);
        return (removed, failed);
    }

    // ---------- Вспомогательное ----------

    private static HashSet<string> CandidateNames(DebloatApp app)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        void AddName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var n = Normalize(raw);
            if (n.Length >= 4) set.Add(n);
        }

        // Имя без версии и разрядности: «Notepad++ (64-bit x64)» → «Notepad++»
        var clean = Regex.Replace(app.DisplayName, @"\s*[\(\[].*?[\)\]]", "");
        clean = Regex.Replace(clean, @"\s+v?\d+([\.\-]\d+)*\b.*$", "", RegexOptions.IgnoreCase).Trim();
        AddName(app.DisplayName);
        AddName(clean);

        if (!string.IsNullOrEmpty(app.InstallLocation))
            AddName(Path.GetFileName(app.InstallLocation.TrimEnd('\\', '/')));

        return set;
    }

    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IEnumerable<string> FolderRoots()
    {
        var list = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        return list.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase)!;
    }

    private static IEnumerable<string> ShortcutRoots()
    {
        var list = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        return list.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase)!;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern).ToList(); }
        catch { return []; }
    }

    /// <summary>Папку можно удалять, только если это не системный/корневой каталог и не родитель такого.</summary>
    public static bool IsSafeDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            var root = Path.GetPathRoot(full)?.TrimEnd('\\', '/') ?? "";
            if (full.Length <= root.Length + 1 || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return false;

            var protectedPaths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Path.Combine(root + "\\", "Users"),
            };

            foreach (var p in protectedPaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                var prot = Path.GetFullPath(p).TrimEnd('\\', '/');
                // сама защищённая папка или её родитель
                if (string.Equals(full, prot, StringComparison.OrdinalIgnoreCase)) return false;
                if (prot.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (full.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return false;

            // Папка игрового лаунчера (или родитель его папки) — никогда не удаляем.
            foreach (var launcherExe in LauncherExecutables)
                if (File.Exists(Path.Combine(full, launcherExe))) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (RegistryHive Hive, RegistryView View, string SubPath)? ParseKey(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var idx = path.IndexOf('\\');
        if (idx < 0) return null;
        var prefix = path[..idx];
        var rest = path[(idx + 1)..];
        return prefix switch
        {
            "HKCU" => (RegistryHive.CurrentUser, RegistryView.Registry64, rest),
            "HKLM64" => (RegistryHive.LocalMachine, RegistryView.Registry64, rest),
            "HKLM32" => (RegistryHive.LocalMachine, RegistryView.Registry32, rest),
            _ => null,
        };
    }

    /// <summary>Путь вида HKLM64\{имя} (из списка программ) → полный путь в ветке Uninstall.</summary>
    private static string? ToFullPath(string? keyPath)
    {
        if (string.IsNullOrEmpty(keyPath)) return null;
        if (keyPath.StartsWith("UNINSTALL:", StringComparison.Ordinal))
            keyPath = keyPath["UNINSTALL:".Length..];
        else
            return keyPath;

        var idx = keyPath.IndexOf('\\');
        return idx < 0 ? null : $"{keyPath[..idx]}\\{UninstallPath}\\{keyPath[(idx + 1)..]}";
    }

    private static bool KeyExists(string? keyPath)
    {
        var parsed = ParseKey(ToFullPath("UNINSTALL:" + keyPath));
        if (parsed is not { } p) return false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(p.Hive, p.View);
            using var key = baseKey.OpenSubKey(p.SubPath);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteRegistryKey(string? keyPath)
    {
        var full = ToFullPath(keyPath);
        var parsed = ParseKey(full);
        if (parsed is not { } p) return;

        // Защита: ключ должен лежать минимум на уровень глубже SOFTWARE и не быть системным
        var segments = p.SubPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var isUninstallEntry = p.SubPath.StartsWith(UninstallPath + "\\", StringComparison.OrdinalIgnoreCase);
        if (!isUninstallEntry)
        {
            if (segments.Length < 2 || !segments[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)
                                    || ProtectedNames.Contains(segments[1]))
                throw new InvalidOperationException("Защищённый раздел реестра: " + full);
        }

        using var baseKey = RegistryKey.OpenBaseKey(p.Hive, p.View);
        baseKey.DeleteSubKeyTree(p.SubPath, throwOnMissingSubKey: false);
    }
}
