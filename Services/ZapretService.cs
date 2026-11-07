using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public sealed record ZapretStatus(bool Installed, bool ServiceRunning, string InstallPath, string Message);

public sealed record ZapretBatInfo(string FileName, string FullPath, string RelativePath, string Kind);

public interface IZapretService
{
    ZapretStatus GetStatus();
    IReadOnlyList<ZapretBatInfo> ListBatFiles();
    Task<(bool Ok, string Message)> TestBatAsync(string fullPath, CancellationToken ct = default);
    /// <summary>Запускает встроенные тесты service.bat (Run Tests) — интерактивно покажет лучшую стратегию.</summary>
    Task<(bool Ok, string Message)> RunOfficialTestsAsync(CancellationToken ct = default);
    /// <summary>По очереди гоняет strategy-bat'ы, проверяет доступность Discord/YouTube, возвращает лучший.</summary>
    Task<(bool Ok, string Message, string? BestBat)> AutoTestStrategiesAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<(bool Ok, string Message)> DownloadAndInstallAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<(bool Ok, string Message)> StartServiceAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> StopServiceAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> UninstallAsync(CancellationToken ct = default);
}

/// <summary>
/// Интеграция с Flowseal/zapret-discord-youtube:
/// скачивает последний release с GitHub, распаковывает и запускает штатные bat-скрипты службы.
/// </summary>
public sealed class ZapretService : IZapretService
{
    private const string RepoApi = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static ZapretService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("WinOptimizer/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public string InstallRoot => Path.Combine(AppPaths.DataDirectory, "zapret");

    public ZapretStatus GetStatus()
    {
        var installed = Directory.Exists(InstallRoot) &&
                        Directory.EnumerateFiles(InstallRoot, "*.bat", SearchOption.AllDirectories).Any();
        var running = IsServiceRunning("zapret") || IsServiceRunning("winws") || ProcessExists("winws");
        var msg = !installed
            ? "Zapret не установлен."
            : running
                ? "Zapret установлен, служба/процесс активны."
                : "Zapret установлен, служба не запущена.";
        return new ZapretStatus(installed, running, InstallRoot, msg);
    }

    public IReadOnlyList<ZapretBatInfo> ListBatFiles()
    {
        if (!Directory.Exists(InstallRoot))
            return [];

        var list = new List<ZapretBatInfo>();
        foreach (var path in Directory.EnumerateFiles(InstallRoot, "*.bat", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            var rel = Path.GetRelativePath(InstallRoot, path);
            // Служебные / опасные для «теста стратегии» пропускаем из рекомендаций, но показываем
            var kind = ClassifyBat(name);
            list.Add(new ZapretBatInfo(name, path, rel, kind));
        }

        return list;
    }

    private static string ClassifyBat(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("service") || n.Contains("install") || n.Contains("uninstall") || n.Contains("remove"))
            return "Служба / установка";
        if (n.Contains("general") || n.Contains("default"))
            return "Базовый (часто самый стабильный)";
        if (n.Contains("alt") || n.Contains("backup"))
            return "Альтернативный";
        if (n.Contains("discord") || n.Contains("youtube") || n.Contains("game"))
            return "Под конкретный сервис";
        if (n.Contains("stop") || n.Contains("kill"))
            return "Остановка";
        return "Стратегия / пресет";
    }

    /// <summary>
    /// Останавливает текущий winws/zapret и запускает выбранный bat для ручного теста.
    /// Пользователь сам проверяет Discord/YouTube; мы только переключаем пресет.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestBatAsync(string fullPath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                return (false, "Файл не найден.");

            try
            {
                KillZapretProcesses();

                var dir = Path.GetDirectoryName(fullPath)!;
                var psi = new ProcessStartInfo
                {
                    FileName = fullPath,
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                    // Не требуем UAC на каждый клик, если уже есть права; иначе Windows сама запросит при необходимости
                    Verb = "",
                };
                try
                {
                    Process.Start(psi);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    psi.Verb = "runas";
                    Process.Start(psi);
                }

                Logger.Info("Zapret test bat: " + fullPath);
                return (true, $"Запущен «{Path.GetFileName(fullPath)}». Проверьте Discord / YouTube. Чтобы сменить стратегию — сначала «Остановить».");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    public Task<(bool Ok, string Message)> RunOfficialTestsAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var service = FindScript(InstallRoot, "service.bat");
            if (service is null)
                return (false, "service.bat не найден. Сначала установите Zapret.");

            // Официальный путь: service.bat → Run Tests → Standard → All → в конце лучшая стратегия.
            // Меню интерактивное — открываем окно пользователю.
            Process.Start(new ProcessStartInfo
            {
                FileName = service,
                WorkingDirectory = Path.GetDirectoryName(service)!,
                UseShellExecute = true,
                Verb = "runas",
            });
            return (true,
                "Открыт service.bat от администратора.\n\n" +
                "Выберите: Run Tests → Standard → All.\n" +
                "В конце утилита Zapret сама выведет самую подходящую стратегию.\n" +
                "Затем: Install Service → выберите найденную стратегию.");
        }, ct);

    /// <summary>
    /// Больше НЕ гоняет bat'ы по кругу (из‑за этого всегда оставался последний winws).
    /// Останавливает процессы и открывает штатный service.bat → Run Tests.
    /// </summary>
    public async Task<(bool Ok, string Message, string? BestBat)> AutoTestStrategiesAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Останавливаю текущий Zapret…");
        KillZapretProcesses();

        progress?.Report("Открываю официальные тесты Zapret…");
        var (ok, msg) = await RunOfficialTestsAsync(ct).ConfigureAwait(false);
        if (!ok)
            return (false, msg, null);

        return (true,
            "Все процессы Zapret остановлены.\n\n" +
            "Открыт service.bat — выберите:\n" +
            "  Run Tests → Standard → All\n\n" +
            "В конце Zapret сам покажет лучшую стратегию.\n" +
            "Ни один bat не запускался автоматически.",
            null);
    }

    public async Task<(bool Ok, string Message)> DownloadAndInstallAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report("Получаю информацию о релизе Flowseal/zapret…");
            var (assetUrl, assetName) = await ResolveLatestAssetAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(assetUrl))
                return (false, "Не удалось найти zip-релиз на GitHub.");

            Directory.CreateDirectory(AppPaths.DownloadsDirectory);
            var zipPath = Path.Combine(AppPaths.DownloadsDirectory, assetName ?? "zapret.zip");

            progress?.Report("Скачиваю архив…");
            await using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var resp = await Http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            progress?.Report("Распаковываю…");
            if (Directory.Exists(InstallRoot))
            {
                try { Directory.Delete(InstallRoot, true); } catch { /* locked files */ }
            }

            Directory.CreateDirectory(InstallRoot);
            ZipFile.ExtractToDirectory(zipPath, InstallRoot, overwriteFiles: true);

            // Иногда релиз кладёт всё в одну подпапку — поднимаем содержимое.
            FlattenSingleRoot(InstallRoot);

            // Не запускаем service.bat / general.bat сами — иначе каждый раз всплывает последний пресет.
            KillZapretProcesses();

            Logger.Info("Zapret установлен в " + InstallRoot);
            return (true,
                "Zapret скачан и распакован.\n\n" +
                "Стратегии не запускались автоматически.\n" +
                "Дальше: «Официальные тесты (service.bat)» или «Протестировать» у нужного bat.");
        }
        catch (Exception ex)
        {
            Logger.Error("Zapret install failed", ex);
            return (false, ex.Message);
        }
    }

    public Task<(bool Ok, string Message)> StartServiceAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var bat = FindScript(InstallRoot, "service.bat", "start.bat", "service_start.bat");
            if (bat != null)
            {
                var r = RunElevatedBat(bat, "start");
                return r.ExitCode == 0
                    ? (true, "Команда запуска отправлена.")
                    : (false, string.IsNullOrWhiteSpace(r.StandardError) ? $"Код {r.ExitCode}" : r.StandardError);
            }

            try
            {
                ProcessRunner.Run("sc.exe", "start zapret", 10_000);
                return (true, "sc start zapret выполнен.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }, ct);

    public Task<(bool Ok, string Message)> StopServiceAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                // Только остановка — без запуска любых bat
                try { ProcessRunner.Run("sc.exe", "stop zapret", 10_000); } catch { }
                try { ProcessRunner.Run("sc.exe", "stop WinDivert", 8_000); } catch { }
                KillZapretProcesses();
                return (true, "Zapret остановлен (служба и процессы winws).");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }, ct);

    public Task<(bool Ok, string Message)> UninstallAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                var bat = FindScript(InstallRoot, "service.bat", "uninstall_service.bat", "service_uninstall.bat");
                if (bat != null)
                    RunElevatedBat(bat, "remove");

                try { ProcessRunner.Run("sc.exe", "stop zapret", 8_000); } catch { }
                try { ProcessRunner.Run("sc.exe", "delete zapret", 8_000); } catch { }

                if (Directory.Exists(InstallRoot))
                    Directory.Delete(InstallRoot, true);

                return (true, "Zapret удалён.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }, ct);

    private async Task<(string? Url, string? Name)> ResolveLatestAssetAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync(RepoApi, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("assets", out var assets))
            return (null, null);

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            return (url, name);
        }

        return (null, null);
    }

    private static void KillZapretProcesses()
    {
        try { ProcessRunner.Run("sc.exe", "stop zapret", 8_000); } catch { }
        foreach (var name in new[] { "winws", "zapret", "winws.exe", "zapret.exe" })
        {
            try
            {
                ProcessRunner.Run("taskkill", $"/F /IM {Path.GetFileName(name)} /T", 5_000);
            }
            catch { /* процесс мог уже завершиться */ }
        }

        // Окна cmd, запущенные с заголовком zapret:
        try
        {
            foreach (var p in Process.GetProcessesByName("cmd"))
            {
                try
                {
                    if (p.MainWindowTitle.Contains("zapret", StringComparison.OrdinalIgnoreCase))
                        p.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string? FindScript(string root, params string[] names)
    {
        foreach (var name in names)
        {
            var hit = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null) return hit;
        }

        return null;
    }

    private static ProcessResult RunElevatedBat(string batPath, string? args = null)
    {
        var dir = Path.GetDirectoryName(batPath)!;
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = string.IsNullOrEmpty(args)
                ? $"/c \"\"{batPath}\"\""
                : $"/c \"\"{batPath}\" {args}\"",
            WorkingDirectory = dir,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(120_000);
            return new ProcessResult(p?.ExitCode ?? -1, "", "");
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    private static void FlattenSingleRoot(string root)
    {
        var dirs = Directory.GetDirectories(root);
        var files = Directory.GetFiles(root);
        if (files.Length == 0 && dirs.Length == 1)
        {
            var only = dirs[0];
            foreach (var entry in Directory.GetFileSystemEntries(only))
            {
                var dest = Path.Combine(root, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                    Directory.Move(entry, dest);
                else
                    File.Move(entry, dest, true);
            }

            try { Directory.Delete(only, true); } catch { }
        }
    }

    private static bool IsServiceRunning(string name)
    {
        try
        {
            var r = ProcessRunner.Run("sc.exe", $"query {name}", 5_000);
            return r.StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool ProcessExists(string name)
    {
        try
        {
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
