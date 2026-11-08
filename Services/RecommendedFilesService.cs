using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

/// <summary>Рекомендуемый файл: что скачать, чем проверить и как запустить.</summary>
public sealed record RecommendedFile(
    string Id,
    string Name,
    string Description,
    string Note,
    string FileName,
    string Sha256,
    string Arguments,
    bool ShowInstallerWindow,
    string? ConfirmText = null,
    IReadOnlyDictionary<string, string>? Environment = null);

public interface IRecommendedFilesService
{
    IReadOnlyList<RecommendedFile> Files { get; }
    /// <summary>Короткий статус «установлено / не найдено» по системе (без скачивания).</summary>
    string GetStatus(RecommendedFile file);
    Task<(bool Ok, string Message)> InstallAsync(RecommendedFile file, IProgress<(string Text, double Percent)>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Рекомендуемые файлы лежат в репозитории (папка recommended). Приложение скачивает файл по кнопке,
/// сверяет SHA-256 и запускает установщик — подменённый или повреждённый файл не запустится.
/// </summary>
public sealed class RecommendedFilesService : IRecommendedFilesService
{
    private const string BaseUrl = "https://raw.githubusercontent.com/reikidod/WinOptimazer/main/recommended/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WinOptimizer/1.0");
        return c;
    }

    public IReadOnlyList<RecommendedFile> Files { get; } =
    [
        new("vc-runtimes",
            "Visual C++ Runtimes (2005–2017, x86 + x64)",
            "Все библиотеки Visual C++, которые нужны играм и программам. Если игра пишет «Не найден MSVCP140.dll» или похожее — это оно.",
            "Пакет Burf v56 (неофициальная сборка, не подписана Microsoft). Перед установкой удалит уже стоящие Visual C++ и поставит свои версии.",
            "vc-runtimes-aio-v56.exe",
            "d36996cb7f936a0ee55ee05de45fcfbff1e829f105bbfa1b2297f3da82d31bc9",
            Arguments: "-y",
            ShowInstallerWindow: true,
            ConfirmText: "Пакет сначала удалит все установленные библиотеки Visual C++ (включая более новые 2015–2022), " +
                         "а затем поставит версии 2005–2017. Закройте игры и программы перед установкой. Продолжить?",
            Environment: new Dictionary<string, string> { ["auto"] = "1" }),

        new("directx",
            "DirectX (веб-установщик Microsoft)",
            "Старые библиотеки DirectX 9/10/11 (d3dx9, xinput, xaudio): без них не запускаются многие игры.",
            "Официальный установщик Microsoft, подписан. Нужен интернет — недостающие файлы он докачивает сам.",
            "dxwebsetup.exe",
            "2cf71d098c608c56e07f4655855a886c3102553f648df88458df616b26fd612f",
            Arguments: "/Q",
            ShowInstallerWindow: false),
    ];

    public string GetStatus(RecommendedFile file) => file.Id switch
    {
        "vc-runtimes" => VcStatus(),
        "directx" => DirectXStatus(),
        _ => "",
    };

    private static string VcStatus()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                     })
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var k = key.OpenSubKey(sub);
                    var name = k?.GetValue("DisplayName") as string;
                    if (name != null && name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
                                     && name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }

            return names.Count > 0
                ? $"Найдено пакетов Visual C++: {names.Count}"
                : "Visual C++ не найден — рекомендуется установить";
        }
        catch
        {
            return "";
        }
    }

    private static string DirectXStatus()
    {
        var sys = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
        var wow = System.Environment.GetFolderPath(System.Environment.SpecialFolder.SystemX86);
        var has64 = File.Exists(Path.Combine(sys, "d3dx9_43.dll"));
        var has32 = File.Exists(Path.Combine(wow, "d3dx9_43.dll"));
        return has64 && has32
            ? "Библиотеки DirectX 9 (d3dx9_43) найдены"
            : "Библиотеки DirectX 9 (d3dx9_43) не найдены — рекомендуется установить";
    }

    public async Task<(bool Ok, string Message)> InstallAsync(
        RecommendedFile file, IProgress<(string Text, double Percent)>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var dir = Path.Combine(AppPaths.DownloadsDirectory, "recommended");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, file.FileName);

            if (!File.Exists(path) || !HashMatches(path, file.Sha256))
            {
                progress?.Report(("Скачиваю…", 0));
                await DownloadAsync(BaseUrl + Uri.EscapeDataString(file.FileName), path, progress, ct).ConfigureAwait(false);
            }

            progress?.Report(("Проверяю целостность файла…", 100));
            if (!HashMatches(path, file.Sha256))
            {
                TryDelete(path);
                return (false, "Контрольная сумма скачанного файла не совпала: файл повреждён или подменён, установка отменена.");
            }

            progress?.Report(("Устанавливаю…", 100));
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = file.Arguments,
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = !file.ShowInstallerWindow,
                WindowStyle = file.ShowInstallerWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            };
            if (file.Environment != null)
                foreach (var (k, v) in file.Environment)
                    psi.Environment[k] = v;

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить установщик.");
            await p.WaitForExitAsync(ct).ConfigureAwait(false);

            Logger.Info($"Рекомендуемый файл {file.Id}: код выхода {p.ExitCode}");
            // 3010 — «успешно, нужна перезагрузка»
            return p.ExitCode is 0 or 3010
                ? (true, $"{file.Name}: установка завершена." + (p.ExitCode == 3010 ? " Нужна перезагрузка." : ""))
                : (false, $"{file.Name}: установщик завершился с кодом {p.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Установка отменена.");
        }
        catch (Exception ex)
        {
            Logger.Error("Рекомендуемый файл: " + file.Id, ex);
            return (false, $"{file.Name}: {ex.Message}");
        }
    }

    private static async Task DownloadAsync(
        string url, string path, IProgress<(string Text, double Percent)>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        var lastReport = DateTime.MinValue;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(($"Скачиваю… {done / 1048576.0:F1} из {total / 1048576.0:F1} МБ", done * 100.0 / total));
            }
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(fs));
            return string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* не критично */ }
    }
}
