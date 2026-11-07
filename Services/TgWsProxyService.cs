using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public sealed record TgWsProxyStatus(bool Installed, bool Running, string InstallPath, string Message);

public interface ITgWsProxyService
{
    TgWsProxyStatus GetStatus();
    /// <summary>Установлен ли прокси самим приложением (иначе — найден в системе и не удаляется).</summary>
    bool IsManagedInstall { get; }
    string LocatorKey { get; }
    Func<string, string?> LocatorProbe { get; }
    /// <summary>Принять путь к exe (или папке), найденный поиском. false — это не TG WS Proxy.</summary>
    bool Adopt(string exeOrFolder);
    Task<(bool Found, string Message)> FindExistingAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<(bool Ok, string Message)> DownloadAndInstallAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<(bool Ok, string Message)> StartAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> StopAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> UninstallAsync(CancellationToken ct = default);
}

/// <summary>
/// Flowseal/tg-ws-proxy — локальный MTProto/WS прокси для Telegram Desktop.
/// Скачивает TgWsProxy_windows.exe с GitHub Releases.
/// </summary>
public sealed class TgWsProxyService : ITgWsProxyService
{
    private const string RepoApi = "https://api.github.com/repos/Flowseal/tg-ws-proxy/releases/latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static TgWsProxyService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("WinOptimizer/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private static readonly Regex ExeName = new(@"^(TgWsProxy|tg-ws-proxy)[^\\/]*\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ProcessNames = { "TgWsProxy_windows", "TgWsProxy", "tg-ws-proxy" };

    private string? _exe;

    private static string ManagedRoot => Path.Combine(AppPaths.DataDirectory, "tg-ws-proxy");
    private static string ManagedExe => Path.Combine(ManagedRoot, "TgWsProxy_windows.exe");

    /// <summary>Папка с найденным exe; если ничего нет — папка для новой установки.</summary>
    public string InstallRoot => _exe is null ? ManagedRoot : Path.GetDirectoryName(_exe)!;
    private string ExePath => _exe ?? ManagedExe;

    public bool IsManagedInstall => _exe is not null &&
        Path.GetFullPath(_exe).StartsWith(Path.GetFullPath(AppPaths.DataDirectory).TrimEnd('\\') + "\\",
            StringComparison.OrdinalIgnoreCase);

    public string LocatorKey => "tgws";

    public Func<string, string?> LocatorProbe => ProbeDirectory;

    private static string? ProbeDirectory(string dir)
    {
        // Скачанный, но не установленный exe во временной папке загрузок установкой не считается
        if (dir.StartsWith(AppPaths.DownloadsDirectory, StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (var file in Directory.EnumerateFiles(dir, "*.exe"))
            if (ExeName.IsMatch(Path.GetFileName(file)))
                return file;
        return null;
    }

    public bool Adopt(string exeOrFolder)
    {
        string? exe = null;
        if (File.Exists(exeOrFolder) && ExeName.IsMatch(Path.GetFileName(exeOrFolder)))
            exe = exeOrFolder;
        else if (Directory.Exists(exeOrFolder))
        {
            try { exe = ProbeDirectory(exeOrFolder); } catch { /* нет доступа */ }
        }

        if (exe is null) return false;
        _exe = exe;
        AppLocator.Remember(LocatorKey, exe);
        Logger.Info("TG WS Proxy найден: " + exe);
        return true;
    }

    private string? ResolveQuick()
    {
        static bool Valid(string? p) => !string.IsNullOrEmpty(p) && File.Exists(p);

        if (Valid(_exe)) return _exe;
        var cached = AppLocator.GetCached(LocatorKey);
        if (Valid(cached)) return cached;
        if (File.Exists(ManagedExe)) return ManagedExe;

        foreach (var path in AppLocator.GetProcessPaths(ProcessNames))
            if (Valid(path)) return path;

        foreach (var path in AppLocator.GetAutorunPaths(ExeName))
            if (Valid(path)) return path;

        return null;
    }

    public async Task<(bool Found, string Message)> FindExistingAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var quick = await Task.Run(ResolveQuick, ct).ConfigureAwait(false);
        if (quick != null)
        {
            _exe = quick;
            AppLocator.Remember(LocatorKey, quick);
            return (true, $"TG WS Proxy найден: {quick}");
        }

        progress?.Report("Ищу TG WS Proxy на компьютере…");
        var hits = await Task.Run(() => AppLocator.Search(
            new Dictionary<string, Func<string, string?>> { [LocatorKey] = ProbeDirectory }, progress, ct), ct)
            .ConfigureAwait(false);
        if (hits.TryGetValue(LocatorKey, out var exe) && Adopt(exe))
            return (true, $"TG WS Proxy найден: {exe}");

        return (false, "TG WS Proxy на компьютере не найден. Можно указать exe вручную или скачать.");
    }

    public TgWsProxyStatus GetStatus()
    {
        _exe = ResolveQuick();
        var installed = _exe is not null;
        var running = ProcessNames.Any(n => Process.GetProcessesByName(n).Length > 0);
        var msg = !installed
            ? "TG WS Proxy не найден."
            : running
                ? "TG WS Proxy найден и запущен (трей). В Telegram: прокси 127.0.0.1:1443 MTProto."
                : "TG WS Proxy найден, сейчас не запущен.";
        return new TgWsProxyStatus(installed, running, InstallRoot, msg);
    }

    public async Task<(bool Ok, string Message)> DownloadAndInstallAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report("Ищу релиз Flowseal/tg-ws-proxy…");
            var (url, name) = await ResolveWindowsExeAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url))
                return (false, "Не найден TgWsProxy_windows.exe в releases GitHub.");

            Directory.CreateDirectory(ManagedRoot);
            Directory.CreateDirectory(AppPaths.DownloadsDirectory);
            var target = ManagedExe;
            var temp = Path.Combine(AppPaths.DownloadsDirectory, name ?? "TgWsProxy_windows.exe");

            progress?.Report("Скачиваю TgWsProxy_windows.exe…");
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            File.Copy(temp, target, overwrite: true);
            _exe = target;
            Logger.Info("TG WS Proxy installed: " + target);
            return (true,
                "TG WS Proxy скачан.\n\n" +
                "Нажмите «Запустить» — приложение уйдёт в трей.\n" +
                "В Telegram Desktop: Настройки → Продвинутые → Тип соединения → MTProto\n" +
                "Сервер 127.0.0.1, порт 1443, secret — из окна/трея прокси.\n" +
                "Или ПКМ по иконке в трее → Open in Telegram.");
        }
        catch (Exception ex)
        {
            Logger.Error("TG WS Proxy install failed", ex);
            return (false, ex.Message);
        }
    }

    public Task<(bool Ok, string Message)> StartAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (!File.Exists(ExePath))
                return (false, "TG WS Proxy не найден. Найдите его на ПК или установите.");

            try
            {
                if (GetStatus().Running)
                    return (true, "Уже запущен.");

                Process.Start(new ProcessStartInfo
                {
                    FileName = ExePath,
                    WorkingDirectory = Path.GetDirectoryName(ExePath),
                    UseShellExecute = true,
                });
                return (true, "Запущен. Смотрите иконку в системном трее.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }, ct);

    public Task<(bool Ok, string Message)> StopAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                foreach (var name in ProcessNames)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                    }
                }

                return (true, "Процессы TG WS Proxy остановлены.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }, ct);

    public Task<(bool Ok, string Message)> UninstallAsync(CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            try
            {
                await StopAsync(ct).ConfigureAwait(false);
                if (!IsManagedInstall)
                {
                    var where = InstallRoot;
                    _exe = null;
                    return (true, "Процесс остановлен. Файлы лежат в папке, которую приложение не устанавливало, " +
                                  "поэтому они не тронуты: " + where);
                }

                if (Directory.Exists(ManagedRoot))
                    Directory.Delete(ManagedRoot, true);
                _exe = null;
                return (true, "TG WS Proxy удалён.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }, ct);

    private static async Task<(string? Url, string? Name)> ResolveWindowsExeAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync(RepoApi, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("assets", out var assets))
            return (null, null);

        string? fallback = null;
        string? fallbackName = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            // Предпочитаем x64 Windows 10+
            if (name.Contains("windows", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("arm", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("7_", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("32", StringComparison.OrdinalIgnoreCase))
            {
                return (url, name);
            }

            fallback ??= url;
            fallbackName ??= name;
        }

        return (fallback, fallbackName);
    }
}
