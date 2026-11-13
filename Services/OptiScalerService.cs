using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public interface IOptiScalerService
{
    string CacheDir { get; }
    bool IsInstalled(string gameExeDir);
    Task<(bool Ok, string Message)> EnsureDownloadedAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<(bool Ok, string Message)> InstallToGameAsync(string gameExePath, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<(bool Ok, string Message)> UninstallFromGameAsync(string gameExePath);
    void SetHighPerformanceGpu(string gameExePath, bool enable);
    bool HasHighPerformanceGpu(string gameExePath);
}

/// <summary>
/// Упрощённый менеджер OptiScaler (идея как у OptiScaler Client):
/// скачать релиз, положить рядом с exe игры как dxgi.dll, бэкап, откат.
/// </summary>
public sealed class OptiScalerService : IOptiScalerService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "WinOptimizer" } },
    };

    private const string ReleaseApi = "https://api.github.com/repos/optiscaler/OptiScaler/releases/latest";
    private static readonly string[] InjectNames = ["dxgi.dll", "winmm.dll", "version.dll", "d3d12.dll"];

    public string CacheDir { get; } = Path.Combine(AppPaths.DataDirectory, "optiscaler");

    public bool IsInstalled(string gameExeDir)
    {
        if (string.IsNullOrEmpty(gameExeDir) || !Directory.Exists(gameExeDir)) return false;
        return InjectNames.Any(n => File.Exists(Path.Combine(gameExeDir, n))
                                    && File.Exists(Path.Combine(gameExeDir, "OptiScaler.ini")));
    }

    public async Task<(bool Ok, string Message)> EnsureDownloadedAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var marker = Path.Combine(CacheDir, "ready.txt");
            if (File.Exists(marker) && Directory.EnumerateFiles(CacheDir, "*.dll").Any())
                return (true, "OptiScaler уже в кэше.");

            progress?.Report("Скачиваю OptiScaler с GitHub…");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(ReleaseApi, ct));
            var assets = doc.RootElement.GetProperty("assets");
            string? url = null;
            string? name = null;
            foreach (var a in assets.EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (!n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                // предпочитаем win / release zip
                url = a.GetProperty("browser_download_url").GetString();
                name = n;
                if (n.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase)) break;
            }
            if (url is null)
                return (false, "В релизе OptiScaler не найден zip.");

            var zipPath = Path.Combine(CacheDir, name ?? "optiscaler.zip");
            progress?.Report($"Загрузка {name}…");
            await using (var fs = File.Create(zipPath))
            {
                await using var stream = await Http.GetStreamAsync(url, ct);
                await stream.CopyToAsync(fs, ct);
            }

            progress?.Report("Распаковка…");
            foreach (var f in Directory.EnumerateFiles(CacheDir))
            {
                if (!f.Equals(zipPath, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
            }
            ZipFile.ExtractToDirectory(zipPath, CacheDir, overwriteFiles: true);
            // если единственная папка — поднять файлы
            FlattenOnce(CacheDir);
            await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("O"), ct);
            return (true, "OptiScaler скачан.");
        }
        catch (Exception ex)
        {
            Logger.Error("OptiScaler download", ex);
            return (false, "Не удалось скачать OptiScaler: " + ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> InstallToGameAsync(
        string gameExePath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(gameExePath))
                return (false, "Файл игры не найден.");

            var dir = Path.GetDirectoryName(gameExePath)!;
            var (ok, msg) = await EnsureDownloadedAsync(progress, ct);
            if (!ok) return (false, msg);

            progress?.Report("Копирую OptiScaler в папку игры…");
            var backupDir = Path.Combine(AppPaths.DataDirectory, "optiscaler-backup",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(dir))[..8]));
            Directory.CreateDirectory(backupDir);

            // исходный dll: OptiScaler.dll или nvngx.dll
            var srcDll = new[] { "OptiScaler.dll", "nvngx.dll" }
                .Select(n => Path.Combine(CacheDir, n))
                .FirstOrDefault(File.Exists);
            if (srcDll is null)
            {
                // поиск во вложенных
                srcDll = Directory.EnumerateFiles(CacheDir, "OptiScaler.dll", SearchOption.AllDirectories).FirstOrDefault()
                         ?? Directory.EnumerateFiles(CacheDir, "nvngx.dll", SearchOption.AllDirectories).FirstOrDefault();
            }
            if (srcDll is null)
                return (false, "В архиве нет OptiScaler.dll.");

            // бэкап существующего dxgi.dll
            var targetName = "dxgi.dll";
            var targetPath = Path.Combine(dir, targetName);
            if (File.Exists(targetPath))
            {
                var bak = Path.Combine(backupDir, targetName + ".bak");
                File.Copy(targetPath, bak, overwrite: true);
            }

            File.Copy(srcDll, targetPath, overwrite: true);

            // ini + остальные dll/файлы рядом (кроме inject dll)
            foreach (var file in Directory.EnumerateFiles(CacheDir))
            {
                var fn = Path.GetFileName(file);
                if (fn.Equals("ready.txt", StringComparison.OrdinalIgnoreCase)) continue;
                if (fn.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (fn.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (fn.Equals("nvngx.dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (InjectNames.Contains(fn, StringComparer.OrdinalIgnoreCase)) continue;

                var dest = Path.Combine(dir, fn);
                if (File.Exists(dest) && !File.Exists(Path.Combine(backupDir, fn)))
                    File.Copy(dest, Path.Combine(backupDir, fn), overwrite: true);
                File.Copy(file, dest, overwrite: true);
            }

            // минимальный ini если нет
            var ini = Path.Combine(dir, "OptiScaler.ini");
            if (!File.Exists(ini))
            {
                await File.WriteAllTextAsync(ini, """
                    [OptiScaler]
                    ; Установлено WinOptimizer. В игре: Insert — меню OptiScaler.
                    """, ct);
            }

            // маркер
            await File.WriteAllTextAsync(Path.Combine(dir, "WinOptimizer.OptiScaler.txt"),
                $"backup={backupDir}\nexe={gameExePath}\n", ct);

            Logger.Info($"OptiScaler установлен: {dir}");
            return (true, "OptiScaler установлен. В игре нажмите Insert для меню. Не используйте в онлайн-играх с античитом.");
        }
        catch (Exception ex)
        {
            Logger.Error("OptiScaler install", ex);
            return (false, ex.Message);
        }
    }

    public Task<(bool Ok, string Message)> UninstallFromGameAsync(string gameExePath)
    {
        try
        {
            if (!File.Exists(gameExePath))
                return Task.FromResult((false, "Файл игры не найден."));

            var dir = Path.GetDirectoryName(gameExePath)!;
            var marker = Path.Combine(dir, "WinOptimizer.OptiScaler.txt");
            string? backupDir = null;
            if (File.Exists(marker))
            {
                foreach (var line in File.ReadAllLines(marker))
                {
                    if (line.StartsWith("backup=", StringComparison.OrdinalIgnoreCase))
                        backupDir = line["backup=".Length..].Trim();
                }
            }

            foreach (var n in InjectNames)
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) File.Delete(p);
            }

            foreach (var extra in new[] { "OptiScaler.ini", "WinOptimizer.OptiScaler.txt", "OptiScaler.log" })
            {
                var p = Path.Combine(dir, extra);
                if (File.Exists(p)) File.Delete(p);
            }

            if (backupDir is not null && Directory.Exists(backupDir))
            {
                foreach (var f in Directory.EnumerateFiles(backupDir))
                {
                    var name = Path.GetFileName(f);
                    if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                        name = name[..^4];
                    File.Copy(f, Path.Combine(dir, name), overwrite: true);
                }
            }

            Logger.Info($"OptiScaler удалён: {dir}");
            return Task.FromResult((true, "OptiScaler удалён, файлы игры восстановлены из резервной копии (если была)."));
        }
        catch (Exception ex)
        {
            Logger.Error("OptiScaler uninstall", ex);
            return Task.FromResult((false, ex.Message));
        }
    }

    public void SetHighPerformanceGpu(string gameExePath, bool enable)
    {
        if (string.IsNullOrWhiteSpace(gameExePath))
            throw new ArgumentException("Не указан путь к исполняемому файлу игры.", nameof(gameExePath));

        var normalizedPath = Path.GetFullPath(gameExePath);
        if (!string.Equals(Path.GetExtension(normalizedPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Для настройки GPU требуется путь к EXE-файлу игры.", nameof(gameExePath));
        if (enable && !File.Exists(normalizedPath))
            throw new FileNotFoundException("Исполняемый файл игры не найден.", normalizedPath);

        const string preferencesPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        if (enable)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(preferencesPath, writable: true)
                ?? throw new InvalidOperationException("Не удалось открыть настройки графики текущего пользователя.");
            key.SetValue(normalizedPath, "GpuPreference=2;", Microsoft.Win32.RegistryValueKind.String);
            key.Flush();

            if (!string.Equals(key.GetValue(normalizedPath) as string, "GpuPreference=2;", StringComparison.Ordinal))
                throw new InvalidOperationException("Windows не подтвердила сохранение режима высокой производительности GPU.");
            return;
        }

        // Режим по умолчанию в Windows означает отсутствие пользовательского переопределения.
        // Не создаём пустой раздел реестра и не считаем отсутствие значения ошибкой.
        using var existingKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(preferencesPath, writable: true);
        if (existingKey is null)
            return;

        existingKey.DeleteValue(normalizedPath, throwOnMissingValue: false);
        existingKey.Flush();
        if (existingKey.GetValue(normalizedPath) is not null)
            throw new InvalidOperationException("Не удалось сбросить пользовательское переопределение GPU.");
    }

    public bool HasHighPerformanceGpu(string gameExePath)
    {
        if (string.IsNullOrWhiteSpace(gameExePath))
            return false;

        string normalizedPath;
        try { normalizedPath = Path.GetFullPath(gameExePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\DirectX\UserGpuPreferences");
        var value = key?.GetValue(normalizedPath) as string;
        return value is not null &&
               value.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase);
    }

    private static void FlattenOnce(string dir)
    {
        var subs = Directory.GetDirectories(dir);
        if (subs.Length != 1) return;
        var sub = subs[0];
        foreach (var f in Directory.EnumerateFiles(sub))
        {
            var dest = Path.Combine(dir, Path.GetFileName(f));
            if (!File.Exists(dest)) File.Move(f, dest);
        }
        foreach (var d in Directory.EnumerateDirectories(sub))
        {
            var dest = Path.Combine(dir, Path.GetFileName(d));
            if (!Directory.Exists(dest)) Directory.Move(d, dest);
        }
    }
}
