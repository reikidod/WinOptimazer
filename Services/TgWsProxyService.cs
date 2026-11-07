using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public sealed record TgWsProxyStatus(bool Installed, bool Running, string InstallPath, string Message);

public interface ITgWsProxyService
{
    TgWsProxyStatus GetStatus();
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

    public string InstallRoot => Path.Combine(AppPaths.DataDirectory, "tg-ws-proxy");
    private string ExePath => Path.Combine(InstallRoot, "TgWsProxy_windows.exe");

    public TgWsProxyStatus GetStatus()
    {
        var installed = File.Exists(ExePath);
        var running = Process.GetProcessesByName("TgWsProxy_windows").Length > 0
                      || Process.GetProcessesByName("TgWsProxy").Length > 0;
        var msg = !installed
            ? "TG WS Proxy не установлен."
            : running
                ? "TG WS Proxy установлен и запущен (трей). В Telegram: прокси 127.0.0.1:1443 MTProto."
                : "TG WS Proxy установлен, сейчас не запущен.";
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

            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(AppPaths.DownloadsDirectory);
            var target = Path.Combine(InstallRoot, "TgWsProxy_windows.exe");
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
                return (false, "Сначала установите TG WS Proxy.");

            try
            {
                if (GetStatus().Running)
                    return (true, "Уже запущен.");

                Process.Start(new ProcessStartInfo
                {
                    FileName = ExePath,
                    WorkingDirectory = InstallRoot,
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
                foreach (var name in new[] { "TgWsProxy_windows", "TgWsProxy", "tg-ws-proxy" })
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
                if (Directory.Exists(InstallRoot))
                    Directory.Delete(InstallRoot, true);
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
