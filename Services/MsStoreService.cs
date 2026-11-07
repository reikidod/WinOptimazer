using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

/// <summary>Результат проверки winget.</summary>
public sealed record WingetStatus(
    bool Available, string? Version, string? Path, bool MsStoreSource, string Message);

/// <summary>Данные карточки из каталога Store: издатель, описание, иконка (PNG/JPEG).</summary>
public sealed record StoreMetadata(string? Publisher, string? Description, byte[]? Icon);

public interface IMsStoreService
{
    IReadOnlyList<StoreAppInfo> Featured { get; }
    bool IsWingetAvailable();
    /// <summary>Полная проверка: найден ли winget, его версия и доступен ли источник msstore.</summary>
    Task<WingetStatus> CheckWingetAsync(CancellationToken ct = default);
    /// <summary>Устанавливает/регистрирует App Installer (winget) и чинит источник msstore.</summary>
    Task<(bool Ok, string Message)> EnsureWingetAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    /// <summary>Иконка, издатель и описание приложения из каталога Store (с кэшем на диске).</summary>
    Task<StoreMetadata?> GetMetadataAsync(StoreAppInfo app, CancellationToken ct = default);
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
        new("9N0DX20HK701", "Windows Terminal", "Microsoft Corporation", "msstore",
            ProductId: "9N0DX20HK701", Description: "Современный терминал от Microsoft."),
        new("9NBLGGH4NNS1", "App Installer / winget", "Microsoft Corporation", "msstore",
            Description: "Установщик пакетов Windows Package Manager."),
        new("9NKSQGP7F2NH", "WhatsApp", "WhatsApp Inc.", "msstore",
            ProductId: "9NKSQGP7F2NH", Description: "Мессенджер WhatsApp из Microsoft Store."),
        new("9NCBCSZSJRSB", "Spotify", "Spotify AB", "msstore",
            ProductId: "9NCBCSZSJRSB", Description: "Музыкальный клиент Spotify."),
        new("9WZDNCRFHVJL", "OneNote", "Microsoft Corporation", "msstore",
            ProductId: "9WZDNCRFHVJL", Description: "Заметки OneNote."),
        new("9PB2MZ1ZMB1S", "iTunes", "Apple Inc.", "msstore",
            Description: "iTunes для Windows (Store)."),
        new("9PDXGNCFSCZV", "Ubuntu", "Canonical Group Limited", "msstore",
            ProductId: "9PDXGNCFSCZV", Description: "Дистрибутив Ubuntu для WSL."),
        new("9WZDNCRFJBH4", "Photos", "Microsoft Corporation", "msstore",
            Description: "Приложение «Фотографии» Microsoft."),
        new("9MVZQVXJBQ9V", "AV1 Video Extension", "Microsoft Corporation", "msstore",
            Description: "Кодек AV1 для видео."),
        new("9NCVDN91XZQP", "Python 3.12", "Python Software Foundation", "msstore",
            Description: "Python из Microsoft Store."),
        new("9NFTCH6J7FHV", "Power Automate", "Microsoft Corporation", "msstore",
            ProductId: "9NFTCH6J7FHV", Description: "Автоматизация задач Windows."),
    ];

    // ---------- winget: поиск и проверка ----------

    private static string? _wingetPath;
    private static string? _wingetVersion;

    /// <summary>
    /// Ищет winget.exe: PATH, псевдоним приложения (WindowsApps) и папка пакета App Installer —
    /// из процесса с правами администратора «winget» в PATH часто не виден.
    /// </summary>
    private static string? ResolveWingetPath(bool forceRefresh = false)
    {
        if (!forceRefresh && _wingetPath != null && File.Exists(_wingetPath)) return _wingetPath;
        _wingetPath = null;
        _wingetVersion = null;

        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), "winget.exe")); } catch { /* некорректный путь */ }
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe"));

        try
        {
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            candidates.AddRange(Directory.EnumerateDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*_8wekyb3d8bbwe")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "winget.exe")));
        }
        catch { /* нет доступа к WindowsApps — не страшно */ }

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(c)) continue;
                var version = RunQuick(c, "--version");
                if (version is null) continue;
                _wingetPath = c;
                _wingetVersion = version;
                return c;
            }
            catch { /* пробуем следующий */ }
        }

        return null;
    }

    /// <summary>Быстрый запуск с ожиданием; возвращает вывод (первая строка) или null, если не запустилось.</summary>
    private static string? RunQuick(string file, string args, int timeoutMs = 8000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* уже завершился */ }
                return null;
            }

            var text = stdout.GetAwaiter().GetResult().Trim();
            return p.ExitCode == 0 && text.Length > 0 ? text.Split('\n')[0].Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public bool IsWingetAvailable() => ResolveWingetPath() != null;

    public async Task<WingetStatus> CheckWingetAsync(CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            var path = ResolveWingetPath(forceRefresh: true);
            if (path is null)
                return new WingetStatus(false, null, null, false,
                    "winget не найден. Нажмите «Установить winget» — приложение поставит App Installer.");

            var (_, sources) = await RunWingetAsync("source list", ct, timeoutMs: 30_000).ConfigureAwait(false);
            var hasStore = sources.Contains("msstore", StringComparison.OrdinalIgnoreCase);
            var msg = hasStore
                ? $"winget {_wingetVersion} работает, источник Microsoft Store подключён."
                : $"winget {_wingetVersion} найден, но источник msstore отсутствует. Нажмите «Починить winget».";
            return new WingetStatus(true, _wingetVersion, path, hasStore, msg);
        }, ct).ConfigureAwait(false);
    }

    public async Task<(bool Ok, string Message)> EnsureWingetAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (ResolveWingetPath(forceRefresh: true) is null)
            {
                progress?.Report("Регистрирую App Installer (winget)…");
                // 1) Пакет уже есть в системе, но не зарегистрирован для пользователя
                await RunPowerShellAsync(
                    "Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe",
                    ct, 120_000).ConfigureAwait(false);

                // 2) Нет — скачиваем актуальный msixbundle (aka.ms/getwinget) и ставим
                if (ResolveWingetPath(forceRefresh: true) is null)
                {
                    progress?.Report("Скачиваю App Installer…");
                    var bundle = Path.Combine(AppPaths.DownloadsDirectory, "Microsoft.DesktopAppInstaller.msixbundle");
                    Directory.CreateDirectory(AppPaths.DownloadsDirectory);
                    using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                    {
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinOptimizer/1.0");
                        await using var fs = File.Create(bundle);
                        await using var stream = await client.GetStreamAsync("https://aka.ms/getwinget", ct).ConfigureAwait(false);
                        await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
                    }

                    progress?.Report("Устанавливаю App Installer…");
                    await RunPowerShellAsync($"Add-AppxPackage -Path '{bundle}' -ForceApplicationShutdown", ct, 300_000)
                        .ConfigureAwait(false);
                }

                if (ResolveWingetPath(forceRefresh: true) is null)
                {
                    OpenUri("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1");
                    return (false,
                        "Не удалось поставить winget автоматически. Открыта страница «Установщик приложений» в Microsoft Store — " +
                        "нажмите там «Получить» или «Обновить», затем «Проверить winget».");
                }
            }

            progress?.Report("Проверяю источник Microsoft Store…");
            var (_, list) = await RunWingetAsync("source list", ct, timeoutMs: 30_000).ConfigureAwait(false);
            if (!list.Contains("msstore", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Сбрасываю источники winget…");
                await RunWingetAsync("source reset --force", ct, timeoutMs: 60_000).ConfigureAwait(false);
            }

            progress?.Report("Обновляю источники winget…");
            await RunWingetAsync("source update", ct, timeoutMs: 120_000).ConfigureAwait(false);

            var status = await CheckWingetAsync(ct).ConfigureAwait(false);
            return (status.Available && status.MsStoreSource, status.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("Установка winget", ex);
            return (false, "Не удалось установить winget: " + ex.Message);
        }
    }

    private static async Task<(int Code, string Output)> RunPowerShellAsync(string script, CancellationToken ct, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(true); } catch { /* уже завершился */ }
            return (-1, "Таймаут PowerShell.");
        }

        return (p.ExitCode, await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false));
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
            if (!IsWingetAvailable())
                return (false, "winget не найден. Нажмите «Установить winget» на этой странице.");

            progress?.Report($"{app.Name}: подготовка…");
            void OnLine(string line) => progress?.Report($"{app.Name}: {line}");

            var id = app.Id.Replace("\"", "");
            var source = string.IsNullOrEmpty(app.Source) ? "" : $" --source {app.Source}";
            var common = " --accept-package-agreements --accept-source-agreements --disable-interactivity";

            var (code, output) = await RunWingetAsync(
                $"install --id \"{id}\" --exact{source}{common}", ct, timeoutMs: 900_000, onLine: OnLine).ConfigureAwait(false);
            if (IsInstallSuccess(code, output))
                return (true, SuccessMessage(app, code, output));

            // Id не найден или источник капризничает — пробуем по имени, потом без указания источника
            if (NotFound(output))
            {
                (code, output) = await RunWingetAsync(
                    $"install --name \"{app.Name.Replace("\"", "")}\" --exact{source}{common}", ct, 900_000, OnLine).ConfigureAwait(false);
                if (IsInstallSuccess(code, output))
                    return (true, SuccessMessage(app, code, output));
            }

            if (!string.IsNullOrEmpty(source))
            {
                (code, output) = await RunWingetAsync(
                    $"install --id \"{id}\" --exact{common}", ct, 900_000, OnLine).ConfigureAwait(false);
                if (IsInstallSuccess(code, output))
                    return (true, SuccessMessage(app, code, output));
            }

            Logger.Error($"winget install {app.Id}: код {code}\n{TrimOutput(output)}");
            return (false, $"Не удалось установить «{app.Name}» через winget (код {code}).\n\n{TrimOutput(output)}");
        }
        catch (OperationCanceledException)
        {
            return (false, "Установка отменена.");
        }
        catch (Exception ex)
        {
            Logger.Error("Store install failed", ex);
            return (false, ex.Message);
        }
    }

    // Коды winget: 0x8A150061 — уже установлено, 0x8A15002B — обновление не требуется.
    private static bool IsAlreadyInstalled(int code, string output) =>
        code is -1978335135 or -1978335189
        || output.Contains("already installed", StringComparison.OrdinalIgnoreCase)
        || output.Contains("уже установлен", StringComparison.OrdinalIgnoreCase)
        || output.Contains("No newer package versions", StringComparison.OrdinalIgnoreCase);

    private static bool IsInstallSuccess(int code, string output) =>
        code == 0
        || output.Contains("successfully installed", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Успешно установлено", StringComparison.OrdinalIgnoreCase)
        || IsAlreadyInstalled(code, output);

    private static string SuccessMessage(StoreAppInfo app, int code, string output) =>
        IsAlreadyInstalled(code, output) && code != 0
            ? $"{app.Name}: уже установлено."
            : $"{app.Name}: установка завершена.";

    private static bool NotFound(string output) =>
        output.Contains("No package found", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Не найден пакет", StringComparison.OrdinalIgnoreCase)
        || output.Contains("No installed package", StringComparison.OrdinalIgnoreCase)
        || output.Contains("не найдено", StringComparison.OrdinalIgnoreCase);

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
        string arguments, CancellationToken ct, int timeoutMs = 120_000, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveWingetPath() ?? "winget",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить winget.");
        var stderrTask = p.StandardError.ReadToEndAsync(ct);

        // Построчное чтение: winget рисует прогресс символами \r — ReadLine делит и по ним
        var sb = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                sb.AppendLine(line);
                var t = line.Trim();
                // пропускаем «крутилку» и пустые строки
                if (t.Length > 1 && !Regex.IsMatch(t, @"^[-\\|/]+$"))
                    onLine?.Invoke(t.Length > 90 ? t[..90] + "…" : t);
            }
        }, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(true); } catch { /* уже завершился */ }
            return (-1, "Таймаут winget.");
        }

        var output = sb + "\n" + await stderrTask.ConfigureAwait(false);
        return (p.ExitCode, output);
    }

    private static List<StoreAppInfo> ParseWingetTable(string output)
    {
        var list = new List<StoreAppInfo>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var started = false;
        int idCol = -1, verCol = -1, srcCol = -1;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (!started)
            {
                var hm = Regex.Match(line, @"Name\s+Id(\s+Version)?");
                if (hm.Success && line.Contains("Id", StringComparison.Ordinal))
                {
                    started = true;
                    var nameCol = line.IndexOf("Name", StringComparison.Ordinal);
                    idCol = line.IndexOf("Id", nameCol + 4, StringComparison.Ordinal);
                    verCol = line.IndexOf("Version", StringComparison.Ordinal);
                    srcCol = line.IndexOf("Source", StringComparison.Ordinal);
                }
                continue;
            }

            if (line.StartsWith("-") && line.All(c => c is '-' or ' ')) continue;
            if (line.Contains("No package found", StringComparison.OrdinalIgnoreCase)) break;

            string name, id, source;
            if (idCol > 0 && line.Length > idCol)
            {
                // по позициям колонок заголовка: имена с пробелами и «…» разбираются точно
                name = line[..idCol].Trim();
                var idEnd = verCol > idCol && line.Length > verCol ? verCol : line.Length;
                id = line[idCol..idEnd].Trim();
                source = srcCol > 0 && line.Length > srcCol ? line[srcCol..].Trim() : "";
            }
            else
            {
                var parts = Regex.Split(line.Trim(), @"\s{2,}");
                if (parts.Length < 2) continue;
                name = parts[0].Trim();
                id = parts[1].Trim();
                source = parts.Length >= 3 ? parts[^1].Trim() : "";
            }

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id) || id.Contains(' ')) continue;
            if (source.Length == 0) source = Regex.IsMatch(id, @"^[0-9A-Z]{12}$") ? "msstore" : "winget";

            list.Add(new StoreAppInfo(id, name, "", source, ProductId: Regex.IsMatch(id, @"^[0-9A-Z]{12}$") ? id : null));
            if (list.Count >= 40) break;
        }
        return list;
    }

    // ---------- Иконки и описание из каталога Store ----------

    private static readonly HttpClient MetaHttp = CreateMetaClient();

    private static HttpClient CreateMetaClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WinOptimizer/1.0");
        return c;
    }

    private static string? ProductIdOf(StoreAppInfo app)
    {
        if (!string.IsNullOrEmpty(app.ProductId)) return app.ProductId;
        return Regex.IsMatch(app.Id, @"^[0-9A-Z]{12}$") ? app.Id : null;
    }

    public async Task<StoreMetadata?> GetMetadataAsync(StoreAppInfo app, CancellationToken ct = default)
    {
        var pid = ProductIdOf(app);
        if (pid is null) return null;

        var dir = Path.Combine(AppPaths.DataDirectory, "store-cache");
        var iconFile = Path.Combine(dir, pid + ".img");
        var metaFile = Path.Combine(dir, pid + ".json");

        try
        {
            // Кэш на диске: не ходим в сеть повторно
            if (File.Exists(metaFile))
            {
                var cached = JsonSerializer.Deserialize<Dictionary<string, string?>>(await File.ReadAllTextAsync(metaFile, ct).ConfigureAwait(false));
                var icon = File.Exists(iconFile) ? await File.ReadAllBytesAsync(iconFile, ct).ConfigureAwait(false) : null;
                if (cached != null)
                    return new StoreMetadata(cached.GetValueOrDefault("publisher"), cached.GetValueOrDefault("description"), icon);
            }

            var url = $"https://storeedgefd.dsx.mp.microsoft.com/v9.0/products/{pid}?market=US&locale=en-US&deviceFamily=Windows.Desktop";
            using var resp = await MetaHttp.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
                .ConfigureAwait(false);

            // Разбор терпимый к форме ответа: Payload может отсутствовать, поля — быть пустыми
            var payload = doc.RootElement.TryGetProperty("Payload", out var pl) ? pl : doc.RootElement;
            string? Str(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;

            var publisher = Str(payload, "PublisherName");
            var description = Str(payload, "ShortDescription") ?? Str(payload, "Description");

            string? iconUrl = null;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("Images", out var images)
                && images.ValueKind == JsonValueKind.Array)
            {
                var all = images.EnumerateArray().ToList();
                foreach (var type in new[] { "Logo", "Tile", "Icon", "Poster" })
                {
                    var img = all.FirstOrDefault(i => string.Equals(Str(i, "ImageType"), type, StringComparison.OrdinalIgnoreCase));
                    iconUrl = Str(img, "Url");
                    if (iconUrl != null) break;
                }

                iconUrl ??= all.Select(i => Str(i, "Url")).FirstOrDefault(u => u != null);
            }

            byte[]? bytes = null;
            if (iconUrl != null)
            {
                if (iconUrl.StartsWith("//")) iconUrl = "https:" + iconUrl;
                if (!iconUrl.Contains('?')) iconUrl += "?w=128&h=128&q=90";
                bytes = await MetaHttp.GetByteArrayAsync(iconUrl, ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(metaFile, JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["publisher"] = publisher,
                ["description"] = description,
            }), ct).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
                await File.WriteAllBytesAsync(iconFile, bytes, ct).ConfigureAwait(false);

            return new StoreMetadata(publisher, description, bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("Каталог Store: " + app.Name, ex);
            return null;
        }
    }

    private static string TrimOutput(string s)
    {
        s = s.Trim();
        if (s.Length > 400) s = s[^400..];
        return s;
    }
}
