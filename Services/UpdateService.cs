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
    string DisplayVersion { get; }
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

    /// <summary>Версия для показа: с буквенной меткой канала (0.4.12-beta), если она задана при сборке.</summary>
    public string DisplayVersion
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info)) return CurrentVersion;
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }

    /// <summary>Приложение поставлено установщиком (рядом лежит unins000.exe), а не распаковано из архива.</summary>
    internal static bool InstalledByInstaller => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>Установленной версии отдаём установщик (он ещё и проверит .NET), портативной — zip, чтобы обновить её на месте.</summary>
    internal static string? PickAsset(IReadOnlyList<(string Name, string Url)> assets, bool installerInstall)
    {
        string? Find(Func<string, bool> match) => assets.FirstOrDefault(a => match(a.Name)).Url;
        var setup = Find(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                              && n.Contains("Setup", StringComparison.OrdinalIgnoreCase));
        var zip = Find(n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var anyExe = Find(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        return installerInstall ? setup ?? zip ?? anyExe : zip ?? setup ?? anyExe;
    }

    /// <summary>
    /// Принимает только ссылки на assets релизов этого репозитория.
    /// GitHub может перенаправить такую ссылку на собственный CDN после начала загрузки.
    /// </summary>
    internal static bool IsTrustedDownloadUrl(string? downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && string.IsNullOrEmpty(uri.UserInfo)
               && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.StartsWith(
                   $"/{DefaultOwner}/{DefaultRepo}/releases/download/",
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Канал, из которого собрана запущенная версия: «beta» по метке в версии, иначе «stable».</summary>
    internal string InstalledChannel => ChannelOfLabel(DisplayVersion);

    internal static string ChannelOfLabel(string label) =>
        label.EndsWith("-beta", StringComparison.OrdinalIgnoreCase) ? AppSettings.BetaChannel : AppSettings.StableChannel;

    /// <summary>
    /// Релиз нужного канала: stable пропускает prerelease; beta предпочитает prerelease (тег -beta),
    /// и только если бет нет — берёт свежий стабильный.
    /// </summary>
    internal static JsonElement? PickRelease(JsonElement releases, bool beta)
    {
        if (releases.ValueKind != JsonValueKind.Array) return null;
        JsonElement? stableFallback = null;
        foreach (var r in releases.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            var pre = r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            if (beta)
            {
                if (pre) return r;
                stableFallback ??= r;
            }
            else if (!pre)
            {
                return r;
            }
        }
        return beta ? stableFallback : null;
    }

    /// <summary>
    /// Предлагать ли обновление. Номера сборок общие для обоих каналов и только растут, поэтому достаточно сравнить версии.
    /// Исключение — возврат с беты на стабильный канал: стабильная версия может быть старше установленной беты.
    /// </summary>
    internal static bool IsUpdate(string current, string installedChannel, string latest, string latestChannel, bool betaSelected)
    {
        var cmp = CompareVersions(current, latest);
        if (cmp < 0) return true;
        return cmp != 0 && !betaSelected
               && installedChannel == AppSettings.BetaChannel && latestChannel == AppSettings.StableChannel;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;
        try
        {
            var beta = AppSettings.Current.UpdateChannel == AppSettings.BetaChannel;
            // Список, а не /releases/latest: latest не отдаёт бета-релизы (они помечены prerelease).
            var url = $"https://api.github.com/repos/{DefaultOwner}/{DefaultRepo}/releases?per_page=20";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(false, current, null, null, null,
                    "Релизы на GitHub пока не опубликованы.");
            }
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (PickRelease(doc.RootElement, beta) is not { } root)
            {
                return new UpdateCheckResult(false, current, null, null, null,
                    beta ? "Релизы на GitHub пока не опубликованы." : "Стабильных релизов пока нет.");
            }
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var latest = NormalizeVersion(tag);
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
            string? download = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                var list = new List<(string Name, string Url)>();
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    var dl = a.GetProperty("browser_download_url").GetString();
                    if (dl is not null) list.Add((name, dl));
                }
                download = PickAsset(list, InstalledByInstaller);
            }

            if (string.IsNullOrEmpty(latest))
            {
                return new UpdateCheckResult(false, current, tag, notes, download,
                    "Не удалось разобрать номер версии релиза.");
            }

            var isPre = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
            var latestChannel = isPre || tag.EndsWith("-beta", StringComparison.OrdinalIgnoreCase)
                ? AppSettings.BetaChannel : AppSettings.StableChannel;
            var update = IsUpdate(current, InstalledChannel, latest, latestChannel, beta);
            var label = latestChannel == AppSettings.BetaChannel ? latest + " (бета)" : latest;
            if (update && download is not null)
            {
                var switching = CompareVersions(current, latest) >= 0;
                return new UpdateCheckResult(true, current, latest, notes, download,
                    switching
                        ? $"Переход на стабильную версию {label} (сейчас {DisplayVersion})."
                        : $"Доступна версия {label} (сейчас {DisplayVersion}).");
            }
            if (update)
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
        string? temporaryDownload = null;
        try
        {
            if (!IsTrustedDownloadUrl(downloadUrl))
                return (false, "Ссылка на обновление не принадлежит релизам WinOptimizer на GitHub.");

            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(AppPaths.DownloadsDirectory);
            var uri = new Uri(downloadUrl, UriKind.Absolute);
            var name = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(name)) name = "WinOptimizer-update.bin";
            var path = Path.Combine(AppPaths.DownloadsDirectory, name);
            temporaryDownload = path + "." + Guid.NewGuid().ToString("N") + ".download";

            progress?.Report("Скачиваю обновление…");
            await using (var fs = new FileStream(
                temporaryDownload, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using var stream = await Http.GetStreamAsync(uri, ct).ConfigureAwait(false);
                await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            // Готовый файл заменяется только после успешной загрузки: обрыв сети не оставит битый asset.
            File.Move(temporaryDownload, path, overwrite: true);
            temporaryDownload = null;

            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Запускаю установщик…");
                var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
                // Inno Setup: тихая установка в ту же папку. После успеха сам перезапустит приложение
                // (ShouldRelaunch в WinOptimizer.iss), поэтому NORELAUNCH не передаём.
                // Нужны права администратора — иначе файлы в Program Files не заменятся.
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{installDir}\"",
                        UseShellExecute = true,
                        Verb = "runas",
                    });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return (false, "Обновление отменено: нужны права администратора для установщика.");
                }

                // Отпускаем мьютекс (AppMutex в iss) и выходим — установщик дождётся и поставит файлы.
                ShutdownApp();
                return (true, "Установщик обновления запущен. Приложение закроется и откроется снова.");
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
                    tasklist /fi "PID eq {pid}" 2>nul | find " {pid} " >nul
                    if not errorlevel 1 (
                        timeout /t 1 /nobreak >nul
                        goto wait
                    )
                    xcopy /y /e /i /q "{extract}\*" "{targetDir}\"
                    if errorlevel 1 (
                        echo xcopy failed
                        exit /b 1
                    )
                    start "" "{Path.Combine(targetDir, Path.GetFileName(exe))}"
                    del "%~f0"
                    """;
                await File.WriteAllTextAsync(bat, content, ct).ConfigureAwait(false);

                // В Program Files нужны права администратора; в пользовательской папке — обычно нет.
                var needAdmin = InstalledByInstaller || targetDir.Contains("Program Files", StringComparison.OrdinalIgnoreCase);
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = bat,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    if (needAdmin)
                        psi.Verb = "runas";
                    Process.Start(psi);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return (false, "Обновление отменено: для замены файлов нужны права администратора.");
                }

                ShutdownApp();
                return (true, "Обновление будет применено после закрытия. Перезапуск…");
            }

            return (false, "Неизвестный формат файла обновления.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.Info("Загрузка обновления отменена пользователем.");
            return (false, "Загрузка обновления отменена.");
        }
        catch (Exception ex)
        {
            Logger.Error("Update install failed", ex);
            return (false, ex.Message);
        }
        finally
        {
            if (temporaryDownload is not null)
            {
                try { File.Delete(temporaryDownload); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    Logger.Error("Не удалось удалить неполный файл обновления", cleanupError);
                }
            }
        }
    }

    private static void ShutdownApp()
    {
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
