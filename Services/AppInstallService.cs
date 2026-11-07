using System.Diagnostics;
using System.Net.Http;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public sealed record AppCatalogItem(
    string Id,
    string Name,
    string Description,
    string DownloadUrl,
    string FileName,
    string Arguments,
    string? DetectPath = null,
    string? DetectRegistry = null,
    string? DetectProcess = null);

public interface IAppInstallService
{
    IReadOnlyList<AppCatalogItem> Catalog { get; }
    bool IsInstalled(AppCatalogItem app);
    Task<(bool Ok, string Message)> DownloadAndInstallAsync(AppCatalogItem app, IProgress<string>? progress = null, CancellationToken ct = default);
}

public sealed class AppInstallService : IAppInstallService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
    };

    static AppInstallService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("WinOptimizer/1.0");
    }

    public IReadOnlyList<AppCatalogItem> Catalog { get; } =
    [
        new("chrome", "Google Chrome", "Браузер Google Chrome (офлайн-установщик).",
            "https://dl.google.com/chrome/install/ChromeStandaloneSetup64.exe",
            "ChromeStandaloneSetup64.exe", "/silent /install",
            DetectPath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            DetectProcess: "chrome"),
        new("steam", "Steam", "Клиент Steam.",
            "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe",
            "SteamSetup.exe", "/S",
            DetectPath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steam.exe"),
            DetectProcess: "steam"),
        new("discord", "Discord", "Discord для Windows.",
            "https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64",
            "DiscordSetup.exe", "-s",
            DetectPath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Discord", "Update.exe"),
            DetectProcess: "Discord"),
        new("telegram", "Telegram", "Telegram Desktop.",
            "https://telegram.org/dl/desktop/win64",
            "tsetup-x64.exe", "/VERYSILENT /NORESTART",
            DetectPath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Telegram Desktop", "Telegram.exe"),
            DetectProcess: "Telegram"),
        new("7zip", "7-Zip", "Архиватор 7-Zip.",
            "https://www.7-zip.org/a/7z2408-x64.exe",
            "7z2408-x64.exe", "/S",
            DetectPath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "7-Zip", "7z.exe")),
        new("vlc", "VLC", "VLC media player.",
            "https://get.videolan.org/vlc/3.0.21/win64/vlc-3.0.21-win64.exe",
            "vlc-3.0.21-win64.exe", "/S",
            DetectPath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "VideoLAN", "VLC", "vlc.exe"),
            DetectProcess: "vlc"),
    ];

    public bool IsInstalled(AppCatalogItem app)
    {
        // 1) Прямой путь
        if (!string.IsNullOrEmpty(app.DetectPath))
        {
            if (File.Exists(app.DetectPath))
                return true;

            // Discord кладёт версии в подпапки app-x.y.z
            if (app.Id == "discord")
            {
                var discordRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord");
                if (Directory.Exists(discordRoot) &&
                    Directory.EnumerateFiles(discordRoot, "Discord.exe", SearchOption.AllDirectories).Any())
                    return true;
            }

            // Chrome иногда в LocalAppData
            if (app.Id == "chrome")
            {
                var alt = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Google", "Chrome", "Application", "chrome.exe");
                if (File.Exists(alt)) return true;
            }

            // Steam x64 path
            if (app.Id == "steam")
            {
                var alt = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Steam", "steam.exe");
                if (File.Exists(alt)) return true;
            }
        }

        // 2) Uninstall registry (32/64)
        if (FindUninstallDisplayName(app.Name) || FindUninstallDisplayName(app.Id))
            return true;

        // Специфичные имена в uninstall
        if (app.Id == "chrome" && (FindUninstallDisplayName("Google Chrome") || FindUninstallDisplayName("Chrome")))
            return true;
        if (app.Id == "7zip" && FindUninstallDisplayName("7-Zip"))
            return true;

        // 3) Процесс запущен — скорее всего установлено
        if (!string.IsNullOrEmpty(app.DetectProcess))
        {
            try
            {
                if (Process.GetProcessesByName(app.DetectProcess).Length > 0)
                    return true;
            }
            catch { /* ignore */ }
        }

        return false;
    }

    public async Task<(bool Ok, string Message)> DownloadAndInstallAsync(
        AppCatalogItem app, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (IsInstalled(app))
            {
                progress?.Report($"{app.Name} уже установлен — пропуск.");
                Logger.Info($"Skip install, already present: {app.Name}");
                return (true, $"{app.Name} уже установлен. Повторная установка не нужна.");
            }

            Directory.CreateDirectory(AppPaths.DownloadsDirectory);
            var target = Path.Combine(AppPaths.DownloadsDirectory, app.FileName);

            progress?.Report($"Скачиваю {app.Name}…");
            await DownloadFileAsync(app.DownloadUrl, target, ct).ConfigureAwait(false);

            progress?.Report($"Устанавливаю {app.Name}…");
            var psi = new ProcessStartInfo
            {
                FileName = target,
                Arguments = app.Arguments,
                UseShellExecute = true,
                Verb = "runas",
            };

            try
            {
                using var p = Process.Start(psi);
                if (p != null)
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                psi.Verb = "";
                using var p = Process.Start(psi);
                if (p != null)
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }

            // После установки ещё раз проверяем
            if (IsInstalled(app))
            {
                Logger.Info($"Установлен: {app.Name}");
                return (true, $"{app.Name}: установлен.");
            }

            Logger.Info($"Установщик завершён: {app.Name}");
            return (true, $"{app.Name}: установщик завершён. Если ярлыка нет — обновите список.");
        }
        catch (Exception ex)
        {
            Logger.Error($"Install failed: {app.Name}", ex);
            return (false, ex.Message);
        }
    }

    private static bool FindUninstallDisplayName(string needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return false;
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (var root in roots)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;
                foreach (var subName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subName);
                        var display = sub?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(display)) continue;
                        if (display.Contains(needle, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { /* skip */ }
                }
            }
            catch { /* skip */ }
        }

        // HKCU installers (Discord, Telegram иногда)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key != null)
            {
                foreach (var subName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subName);
                        var display = sub?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrEmpty(display) &&
                            display.Contains(needle, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
            }
        }
        catch { }

        return false;
    }

    private static async Task DownloadFileAsync(string url, string path, CancellationToken ct)
    {
        // Редиректы ведём вручную: стандартный клиент не переходит с https на http,
        // а зеркала (например, get.videolan.org) именно так и отвечают (302).
        var current = new Uri(url);
        for (var hop = 0; hop < 10; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            resp.EnsureSuccessStatusCode();
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            return;
        }

        throw new HttpRequestException("Слишком много перенаправлений при скачивании: " + url);
    }
}
