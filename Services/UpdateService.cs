using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseNotes,
    string? DownloadUrl,
    string Message);

public interface IUpdateService
{
    string CurrentVersion { get; }
    string GitHubRepo { get; }
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> DownloadAndInstallAsync(string downloadUrl, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Проверка и установка обновлений с GitHub Releases (reikidod/WinOptimazer).
/// </summary>
public sealed class UpdateService : IUpdateService
{
    public const string DefaultOwner = "reikidod";
    public const string DefaultRepo = "WinOptimazer";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "WinOptimizer-Updater" } },
    };

    public string GitHubRepo => $"{DefaultOwner}/{DefaultRepo}";

    public string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is null) return "0.0.0";
            // 0.3.0.0 -> 0.3.0
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;
        try
        {
            var url = $"https://api.github.com/repos/{DefaultOwner}/{DefaultRepo}/releases/latest";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(false, current, null, null, null,
                    "Релизы на GitHub пока не опубликованы.");
            }
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var latest = NormalizeVersion(tag);
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
            string? download = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    var dl = a.GetProperty("browser_download_url").GetString();
                    if (dl is null) continue;
                    // предпочитаем установщик / setup / publish zip
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("WinOptimizer", StringComparison.OrdinalIgnoreCase)))
                    {
                        download = dl;
                        break;
                    }
                    if (download is null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        download = dl;
                    if (download is null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        download = dl;
                }
            }

            if (string.IsNullOrEmpty(latest))
            {
                return new UpdateCheckResult(false, current, tag, notes, download,
                    "Не удалось разобрать номер версии релиза.");
            }

            var cmp = CompareVersions(current, latest);
            if (cmp < 0 && download is not null)
            {
                return new UpdateCheckResult(true, current, latest, notes, download,
                    $"Доступна версия {latest} (сейчас {current}).");
            }
            if (cmp < 0)
            {
                return new UpdateCheckResult(true, current, latest, notes, null,
                    $"Доступна версия {latest}, но файл установщика в релизе не найден.");
            }

            return new UpdateCheckResult(false, current, latest, notes, download,
                $"У вас актуальная версия {current}.");
        }
        catch (Exception ex)
        {
            Logger.Error("Update check failed", ex);
            return new UpdateCheckResult(false, current, null, null, null,
                "Не удалось проверить обновления: " + ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> DownloadAndInstallAsync(
        string downloadUrl, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DownloadsDirectory);
            var name = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
            if (string.IsNullOrWhiteSpace(name)) name = "WinOptimizer-update.bin";
            var path = Path.Combine(AppPaths.DownloadsDirectory, name);

            progress?.Report("Скачиваю обновление…");
            await using (var fs = File.Create(path))
            {
                await using var stream = await Http.GetStreamAsync(downloadUrl, ct).ConfigureAwait(false);
                await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Запускаю установщик…");
                // Тихая установка если Inno Setup
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "/VERYSILENT /NORESTART /CLOSEAPPLICATIONS",
                    UseShellExecute = true,
                });
                // закрыть текущее приложение через небольшой bat
                ScheduleRestartAndExit(path);
                return (true, "Установщик обновления запущен. Приложение закроется.");
            }

            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Распаковываю обновление…");
                var extract = Path.Combine(AppPaths.DownloadsDirectory, "update-extract");
                if (Directory.Exists(extract)) Directory.Delete(extract, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(path, extract);
                var exe = Directory.EnumerateFiles(extract, "WinOptimizer.exe", SearchOption.AllDirectories).FirstOrDefault()
                           ?? Directory.EnumerateFiles(extract, "*.exe", SearchOption.AllDirectories)
                               .FirstOrDefault(f => !f.Contains("Setup", StringComparison.OrdinalIgnoreCase));
                if (exe is null)
                    return (false, "В архиве не найден WinOptimizer.exe.");

                var targetDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
                var bat = Path.Combine(AppPaths.DownloadsDirectory, "apply-update.bat");
                var pid = Environment.ProcessId;
                var content = $"""
                    @echo off
                    chcp 65001 >nul
                    :wait
                    tasklist /fi "PID eq {pid}" 2>nul | find "{pid}" >nul
                    if not errorlevel 1 (
                        timeout /t 1 /nobreak >nul
                        goto wait
                    )
                    xcopy /y /e /i /q "{extract}\*" "{targetDir}\"
                    start "" "{Path.Combine(targetDir, Path.GetFileName(exe))}"
                    del "%~f0"
                    """;
                await File.WriteAllTextAsync(bat, content, ct).ConfigureAwait(false);
                try
                {
                    // Программа обычно стоит в Program Files — для замены файлов нужны права администратора.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = bat,
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return (false, "Обновление отменено: для замены файлов нужны права администратора.");
                }

                return (true, "Обновление будет применено после закрытия. Перезапуск…");
            }

            return (false, "Неизвестный формат файла обновления.");
        }
        catch (Exception ex)
        {
            Logger.Error("Update install failed", ex);
            return (false, ex.Message);
        }
    }

    private static void ScheduleRestartAndExit(string setupPath)
    {
        try
        {
            var bat = Path.Combine(AppPaths.DownloadsDirectory, "close-and-update.bat");
            File.WriteAllText(bat, $"""
                @echo off
                timeout /t 1 /nobreak >nul
                """);
            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { /* ignore */ }

        // выходим из UI-потока
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });
    }

    internal static string NormalizeVersion(string tag)
    {
        tag = tag.Trim();
        if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            tag = tag[1..];
        var m = Regex.Match(tag, @"\d+(\.\d+){0,3}");
        return m.Success ? m.Value : tag;
    }

    internal static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) =>
            v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var pa = Parts(a);
        var pb = Parts(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var xa = i < pa.Length ? pa[i] : 0;
            var xb = i < pb.Length ? pb[i] : 0;
            if (xa != xb) return xa.CompareTo(xb);
        }
        return 0;
    }
}
