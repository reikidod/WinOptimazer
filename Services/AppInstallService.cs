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
    Task<(bool Ok, string Message)> UninstallAsync(AppCatalogItem app, IProgress<string>? progress = null, CancellationToken ct = default);
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
        // 1) Проверяем фактический исполняемый файл. Для Discord Update.exe
        // может оставаться после удаления, поэтому проверяем только Discord.exe.
        if (!string.IsNullOrEmpty(app.DetectPath))
        {
            if (app.Id != "discord" && File.Exists(app.DetectPath))
                return true;

            if (app.Id == "discord")
            {
                var discordRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord");
                try
                {
                    if (Directory.Exists(discordRoot) &&
                        Directory.EnumerateFiles(discordRoot, "Discord.exe", SearchOption.AllDirectories).Any())
                        return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Error("Не удалось проверить каталог Discord", ex);
                }
            }

            if (app.Id == "chrome")
            {
                var alt = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Google", "Chrome", "Application", "chrome.exe");
                if (File.Exists(alt)) return true;
            }

            if (app.Id == "steam")
            {
                var alt = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Steam", "steam.exe");
                if (File.Exists(alt)) return true;
            }
        }

        // 2) Приложение считается установленным только при наличии записи удаления.
        // Один лишь запущенный процесс не должен переключать кнопку на «Удалить»:
        // процесс может ещё жить после удаления или принадлежать другой копии приложения.
        return FindUninstallEntry(app) is not null;
    }

    public async Task<(bool Ok, string Message)> DownloadAndInstallAsync(
        AppCatalogItem app, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var downloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinOptimizer", "Downloads");
        var target = Path.Combine(downloadDirectory, app.FileName);
        var temporary = target + ".download";
        try
        {
            if (IsInstalled(app)) return (true, $"{app.Name} уже установлен.");
            Directory.CreateDirectory(downloadDirectory);
            progress?.Report($"Скачиваю {app.Name}…");
            await DownloadFileAsync(app.DownloadUrl, temporary, ct).ConfigureAwait(false);
            await using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var signature = new byte[2];
                if (await stream.ReadAsync(signature, ct).ConfigureAwait(false) != 2 || signature[0] != (byte)'M' || signature[1] != (byte)'Z')
                    throw new InvalidDataException("Скачанный файл не является Windows EXE.");
            }
            File.Move(temporary, target, overwrite: true);
            progress?.Report($"Запускаю установщик {app.Name}…");
            var psi = new ProcessStartInfo { FileName = target, Arguments = app.Arguments, UseShellExecute = true, WorkingDirectory = downloadDirectory };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Windows не смогла запустить установщик.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (IsInstalled(app)) return (true, $"{app.Name}: установка подтверждена Windows.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            Logger.Info($"Установка {app.Name} не подтверждена. Код завершения: {process.ExitCode}.");
            return (false, process.ExitCode == 0
                ? $"{app.Name}: установщик завершился, но Windows не обнаружила приложение. Проверьте мастер установки."
                : $"{app.Name}: установщик завершился с кодом {process.ExitCode}.");
        }
        catch (OperationCanceledException) { return (false, $"Установка {app.Name} отменена."); }
        catch (Exception ex)
        {
            Logger.Error($"Install failed: {app.Name}", ex);
            return (false, $"{app.Name}: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Error("Не удалось удалить временный установщик", ex); }
        }
    }

    public async Task<(bool Ok, string Message)> UninstallAsync(
        AppCatalogItem app, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var entry = FindUninstallEntry(app);
            if (entry is null) return (false, $"Для {app.Name} не найдена команда удаления в реестре Windows.");
            var command = !string.IsNullOrWhiteSpace(entry.QuietUninstallString) ? entry.QuietUninstallString : entry.UninstallString;
            if (string.IsNullOrWhiteSpace(command)) return (false, $"Windows не предоставила команду удаления для {app.Name}.");
            var (executable, arguments) = SplitCommandLine(command);
            executable = Environment.ExpandEnvironmentVariables(executable);
            arguments = Environment.ExpandEnvironmentVariables(arguments);
            if (executable.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
                executable.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                executable = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                arguments = System.Text.RegularExpressions.Regex.Replace(arguments, @"(?i)(^|\s)/i(?=\s|\{)", "$1/x");
            }
            if (!File.Exists(executable) && !executable.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                return (false, $"Файл деинсталлятора не найден: {executable}");
            progress?.Report($"Удаляю {app.Name}…");
            var psi = new ProcessStartInfo { FileName = executable, Arguments = arguments, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.SystemDirectory };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Windows не смогла запустить деинсталлятор.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            for (var attempt = 0; attempt < 6; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsInstalled(app)) return (true, $"{app.Name} удалён.");
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            return (false, $"Деинсталлятор завершился с кодом {process.ExitCode}, но {app.Name} всё ещё обнаруживается. Возможно, требуется перезагрузка.");
        }
        catch (OperationCanceledException) { return (false, $"Удаление {app.Name} отменено."); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return (false, "Запрос прав или запуск деинсталлятора отменён пользователем."); }
        catch (Exception ex)
        {
            Logger.Error($"Uninstall failed: {app.Name}", ex);
            return (false, $"{app.Name}: {ex.Message}");
        }
    }

    private sealed record UninstallEntry(string DisplayName, string UninstallString, string? QuietUninstallString);

    private static UninstallEntry? FindUninstallEntry(AppCatalogItem app)
    {
        var needles = app.Id switch
        {
            "chrome" => new[] { "Google Chrome", "Chrome" },
            "steam" => new[] { "Steam" },
            "discord" => new[] { "Discord" },
            "telegram" => new[] { "Telegram Desktop", "Telegram" },
            "7zip" => new[] { "7-Zip" },
            "vlc" => new[] { "VLC media player", "VLC" },
            _ => new[] { app.Name }
        };
        var roots = new[] {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32)
        };
        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var subName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = uninstall.OpenSubKey(subName);
                        var display = sub?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(display) || !MatchesCatalogName(app, display, needles)) continue;
                        var uninstallString = sub?.GetValue("UninstallString") as string;
                        var quiet = sub?.GetValue("QuietUninstallString") as string;
                        if (!string.IsNullOrWhiteSpace(uninstallString) || !string.IsNullOrWhiteSpace(quiet))
                            return new UninstallEntry(display, uninstallString ?? quiet!, quiet);
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            { Logger.Error($"Не удалось прочитать список удаления {hive}/{view}", ex); }
        }
        return null;
    }

    private static bool MatchesCatalogName(AppCatalogItem app, string display, IReadOnlyList<string> names)
    {
        if (names.Any(name => string.Equals(display.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            return true;

        return app.Id switch
        {
            "chrome" => display.StartsWith("Google Chrome ", StringComparison.OrdinalIgnoreCase),
            "7zip" => display.StartsWith("7-Zip ", StringComparison.OrdinalIgnoreCase),
            "telegram" => display.StartsWith("Telegram Desktop ", StringComparison.OrdinalIgnoreCase),
            "vlc" => display.StartsWith("VLC media player ", StringComparison.OrdinalIgnoreCase),
            "discord" => display.StartsWith("Discord ", StringComparison.OrdinalIgnoreCase),
            "steam" => display.StartsWith("Steam ", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static (string Executable, string Arguments) SplitCommandLine(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 1) return (command[1..end], command[(end + 1)..].Trim());
        }
        var exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd >= 0) return (command[..(exeEnd + 4)].Trim(), command[(exeEnd + 4)..].Trim());
        var firstSpace = command.IndexOf(' ');
        return firstSpace < 0 ? (command, "") : (command[..firstSpace], command[(firstSpace + 1)..]);
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
