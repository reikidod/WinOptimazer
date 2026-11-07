using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public sealed record StoreAppInfo(
    string Id,
    string Name,
    string Publisher,
    string Source,
    string? ProductId = null,
    string? Description = null);

public interface IMsStoreService
{
    IReadOnlyList<StoreAppInfo> Featured { get; }
    bool IsWingetAvailable();
    Task<IReadOnlyList<StoreAppInfo>> SearchAsync(string query, CancellationToken ct = default);
    Task<(bool Ok, string Message)> InstallAsync(StoreAppInfo app, IProgress<string>? progress = null, CancellationToken ct = default);
    void OpenInOfficialStore(StoreAppInfo app);
    void OpenStoreHome();
    void OpenStoreUpdates();
    void OpenStoreLibrary();
}

/// <summary>
/// Обёртка над Microsoft Store: избранные приложения, поиск/установка через winget (msstore),
/// и глубокие ссылки ms-windows-store:// для официального клиента.
/// </summary>
public sealed class MsStoreService : IMsStoreService
{
    public IReadOnlyList<StoreAppInfo> Featured { get; } =
    [
        new("9WZDNCRFJBMP", "Windows Terminal", "Microsoft Corporation", "msstore",
            ProductId: "9N0DX20HK701", Description: "Современный терминал от Microsoft."),
        new("9NBLGGH4NNS1", "App Installer / winget", "Microsoft Corporation", "msstore",
            Description: "Установщик пакетов Windows Package Manager."),
        new("9NKSQGP7F2NH", "WhatsApp", "WhatsApp Inc.", "msstore",
            ProductId: "9NKSQGP7F2NH", Description: "Мессенджер WhatsApp из Microsoft Store."),
        new("9NCBCSZSJRSB", "Spotify", "Spotify AB", "msstore",
            ProductId: "9NCBCSZSJRSB", Description: "Музыкальный клиент Spotify."),
        new("9WZDNCRFJBBG", "OneNote", "Microsoft Corporation", "msstore",
            ProductId: "9WZDNCRFJBBG", Description: "Заметки OneNote."),
        new("9NBLGGH4MSV6", "iTunes", "Apple Inc.", "msstore",
            Description: "iTunes для Windows (Store)."),
        new("9P7KNL5RWT25", "Ubuntu", "Canonical Group Limited", "msstore",
            ProductId: "9PDXGNCFSCZV", Description: "Дистрибутив Ubuntu для WSL."),
        new("9NBLGGH537BL", "Photos", "Microsoft Corporation", "msstore",
            Description: "Приложение «Фотографии» Microsoft."),
        new("9NZBFMGTFJJM", "AV1 Video Extension", "Microsoft Corporation", "msstore",
            Description: "Кодек AV1 для видео."),
        new("9N4D0MSBFDRG", "Python 3.12", "Python Software Foundation", "msstore",
            Description: "Python из Microsoft Store."),
        new("9NBLGGH4R0X6", "Power Automate", "Microsoft Corporation", "msstore",
            ProductId: "9NBLGGH4R0X6", Description: "Автоматизация задач Windows."),
    ];

    public bool IsWingetAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0 || !string.IsNullOrWhiteSpace(p.StandardOutput.ReadToEnd());
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<StoreAppInfo>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Featured;

        if (!IsWingetAvailable())
        {
            // Локальный фильтр избранного
            return Featured
                .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || a.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || (a.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        try
        {
            // winget search --source msstore --accept-source-agreements
            var args =
                $"search \"{query.Replace("\"", "")}\" --source msstore --accept-source-agreements --disable-interactivity";
            var (code, output) = await RunWingetAsync(args, ct).ConfigureAwait(false);
            var parsed = ParseWingetTable(output);
            if (parsed.Count > 0)
                return parsed;

            // fallback without source filter
            args = $"search \"{query.Replace("\"", "")}\" --accept-source-agreements --disable-interactivity";
            (_, output) = await RunWingetAsync(args, ct).ConfigureAwait(false);
            parsed = ParseWingetTable(output);
            return parsed.Count > 0 ? parsed : Featured
                .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error("Store search failed", ex);
            return Featured
                .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public async Task<(bool Ok, string Message)> InstallAsync(
        StoreAppInfo app, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (IsWingetAvailable())
            {
                progress?.Report($"Установка {app.Name} через winget…");
                var id = app.Id.Replace("\"", "");
                var args =
                    $"install --id \"{id}\" --source msstore --accept-package-agreements --accept-source-agreements --disable-interactivity";
                var (code, output) = await RunWingetAsync(args, ct, timeoutMs: 600_000).ConfigureAwait(false);
                if (code == 0 || output.Contains("successfully installed", StringComparison.OrdinalIgnoreCase)
                              || output.Contains("Успешно установлено", StringComparison.OrdinalIgnoreCase))
                {
                    return (true, $"{app.Name}: установка завершена.");
                }

                // Попробовать без source
                args =
                    $"install --id \"{id}\" --accept-package-agreements --accept-source-agreements --disable-interactivity";
                (code, output) = await RunWingetAsync(args, ct, timeoutMs: 600_000).ConfigureAwait(false);
                if (code == 0 || output.Contains("successfully installed", StringComparison.OrdinalIgnoreCase))
                    return (true, $"{app.Name}: установка завершена.");

                return (false,
                    $"Не удалось установить «{app.Name}» через winget.\n\n{TrimOutput(output)}");
            }

            return (false,
                "winget не найден. Установите App Installer — установка выполняется только в WinOptimizer, без открытия Microsoft Store.");
        }
        catch (Exception ex)
        {
            Logger.Error("Store install failed", ex);
            return (false, ex.Message);
        }
    }

    public void OpenInOfficialStore(StoreAppInfo app)
    {
        var uri = !string.IsNullOrEmpty(app.ProductId)
            ? $"ms-windows-store://pdp/?ProductId={app.ProductId}"
            : !string.IsNullOrEmpty(app.Id) && app.Id.Length >= 10
                ? $"ms-windows-store://pdp/?ProductId={app.Id}"
                : $"ms-windows-store://search/?query={Uri.EscapeDataString(app.Name)}";
        OpenUri(uri);
    }

    public void OpenStoreHome() => OpenUri("ms-windows-store://home");
    public void OpenStoreUpdates() => OpenUri("ms-windows-store://downloadsandupdates");
    public void OpenStoreLibrary() => OpenUri("ms-windows-store://collection?collectionId=ownedApps");

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("Open store URI failed: " + uri, ex);
            // fallback web
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://apps.microsoft.com/",
                    UseShellExecute = true,
                });
            }
            catch { /* ignore */ }
        }
    }

    private static async Task<(int Code, string Output)> RunWingetAsync(
        string arguments, CancellationToken ct, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить winget.");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(true); } catch { }
            return (-1, "Таймаут winget.");
        }

        var output = (await stdoutTask.ConfigureAwait(false)) + "\n" + (await stderrTask.ConfigureAwait(false));
        return (p.ExitCode, output);
    }

    private static List<StoreAppInfo> ParseWingetTable(string output)
    {
        var list = new List<StoreAppInfo>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var started = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("Name", StringComparison.OrdinalIgnoreCase)
                && line.Contains("Id", StringComparison.OrdinalIgnoreCase))
            {
                started = true;
                continue;
            }
            if (!started) continue;
            if (line.StartsWith("-") && line.All(c => c is '-' or ' ')) continue;
            if (line.Contains("No package found", StringComparison.OrdinalIgnoreCase)) break;

            // Name can have spaces; Id is typically last tokens before Version/Source
            // Format: Name  Id  Version  [Match]  Source
            var parts = Regex.Split(line.Trim(), @"\s{2,}");
            if (parts.Length < 2) continue;
            var name = parts[0].Trim();
            var id = parts[1].Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id)) continue;
            if (name.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;

            var source = parts.Length >= 3 ? parts[^1].Trim() : "msstore";
            list.Add(new StoreAppInfo(id, name, "", source, ProductId: id.Length == 12 ? id : null));
            if (list.Count >= 40) break;
        }
        return list;
    }

    private static string TrimOutput(string s)
    {
        s = s.Trim();
        if (s.Length > 400) s = s[^400..];
        return s;
    }
}
