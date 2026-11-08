using System.Net.Http;
using System.Text.RegularExpressions;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public interface IBenchmarkService
{
    Task<HardwareScores> GetScoresAsync(HardwareProfile hw, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Оценки комплектующих (баллы PassMark G3D / CPU Mark). Таблицы скачиваются из интернета и кэшируются на 30 дней.
/// Если интернета нет или модели нет в таблице (например, новые RTX 40/50), используется оценка по названию или по ядрам.
/// </summary>
public sealed class BenchmarkService : IBenchmarkService
{
    private static readonly string[] GpuUrls =
    {
        "https://raw.githubusercontent.com/CyberDataLab/nebula/main/nebula/addons/trustworthiness/benchmarks/GPU_benchmarks_v7.csv",
        "https://raw.githubusercontent.com/thiagoyanagimori/gpu-kdd-project/main/GPU_benchmarks_v7.csv",
    };

    private static readonly string[] CpuUrls =
    {
        "https://raw.githubusercontent.com/CyberDataLab/nebula/main/nebula/addons/trustworthiness/benchmarks/CPU_benchmarks_v4.csv",
        "https://raw.githubusercontent.com/mrognor/MVault/main/Resources/Csv/CPU_benchmark_v4.csv",
    };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WinOptimizer/1.0");
        return c;
    }

    public async Task<HardwareScores> GetScoresAsync(HardwareProfile hw, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Загружаю таблицы производительности…");
        var gpuTable = ParseTable(await LoadCsvAsync("gpu.csv", GpuUrls, ct).ConfigureAwait(false), "gpuName", "G3Dmark", NormalizeGpu);
        var cpuTable = ParseTable(await LoadCsvAsync("cpu.csv", CpuUrls, ct).ConfigureAwait(false), "cpuName", "cpuMark", NormalizeCpu);
        return Resolve(hw, gpuTable, cpuTable);
    }

    internal static HardwareScores Resolve(HardwareProfile hw, Dictionary<string, double> gpuTable, Dictionary<string, double> cpuTable)
    {
        double? gpu = null;
        var gpuSource = "не определена";
        if (!string.IsNullOrWhiteSpace(hw.GpuName))
        {
            if (Find(gpuTable, NormalizeGpu(hw.GpuName)) is { } hit)
            {
                gpu = hit;
                gpuSource = $"{hit:0} баллов PassMark (интернет)";
            }
            else if (EstimateGpu(hw.GpuName) is { } est)
            {
                gpu = est;
                gpuSource = $"≈{est:0} баллов PassMark (оценка по названию модели)";
            }
        }

        double? cpu = null;
        var cpuSource = "не определён";
        if (!string.IsNullOrWhiteSpace(hw.CpuName))
        {
            if (Find(cpuTable, NormalizeCpu(hw.CpuName)) is { } hit)
            {
                cpu = hit;
                cpuSource = $"{hit:0} баллов PassMark (интернет)";
            }
            else if (hw.CpuThreads > 0 && hw.CpuMaxGhz > 0)
            {
                cpu = hw.CpuThreads * hw.CpuMaxGhz * 360;
                cpuSource = $"≈{cpu:0} баллов (оценка по потокам и частоте)";
            }
        }

        return new HardwareScores(gpu, gpuSource, cpu, cpuSource);
    }

    // ---------- Разбор таблиц ----------

    internal static string NormalizeGpu(string name)
    {
        var s = name.ToLowerInvariant();
        s = Regex.Replace(s, @"\((r|tm)\)|®|™", "");
        s = Regex.Replace(s, @"\b(nvidia|amd|corporation)\b", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    internal static string NormalizeCpu(string name)
    {
        var s = name.ToLowerInvariant();
        s = Regex.Replace(s, @"\((r|tm)\)|®|™", "");
        s = Regex.Replace(s, @"\b\d+(st|nd|rd|th) gen\b", "");
        s = Regex.Replace(s, @"\b(intel|amd|cpu|processor)\b", "");
        s = Regex.Replace(s, @"@\s*[\d.]+\s*ghz", "");
        s = Regex.Replace(s, @"\b\d+-core\b", "");
        s = Regex.Replace(s, @"\bwith radeon[\w ]*graphics\b", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    internal static Dictionary<string, double> ParseTable(string? csv, string nameColumn, string scoreColumn, Func<string, string> normalize)
    {
        var table = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(csv)) return table;

        var rows = ParseCsv(csv);
        if (rows.Count < 2) return table;
        var nameIdx = Array.FindIndex(rows[0], c => c.Trim().Equals(nameColumn, StringComparison.OrdinalIgnoreCase));
        var scoreIdx = Array.FindIndex(rows[0], c => c.Trim().Equals(scoreColumn, StringComparison.OrdinalIgnoreCase));
        if (nameIdx < 0 || scoreIdx < 0) return table;

        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Length <= Math.Max(nameIdx, scoreIdx)) continue;
            if (!double.TryParse(r[scoreIdx], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var score) || score <= 0) continue;
            var key = normalize(r[nameIdx]);
            if (key.Length == 0) continue;
            if (!table.TryGetValue(key, out var old) || score > old) table[key] = score;
        }

        return table;
    }

    internal static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(sb.ToString()); sb.Clear();
                if (fields.Count > 1 || fields[0].Length > 0) rows.Add(fields.ToArray());
                fields.Clear();
            }
            else sb.Append(c);
        }

        if (sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(sb.ToString());
            rows.Add(fields.ToArray());
        }

        return rows;
    }

    /// <summary>Точное совпадение, иначе самая длинная запись таблицы, входящая в название.</summary>
    internal static double? Find(Dictionary<string, double> table, string normalized)
    {
        if (normalized.Length == 0) return null;
        if (table.TryGetValue(normalized, out var exact)) return exact;

        string? bestKey = null;
        foreach (var key in table.Keys)
            if (key.Length >= 6 && normalized.Contains(key, StringComparison.Ordinal) && (bestKey is null || key.Length > bestKey.Length))
                bestKey = key;
        return bestKey is null ? null : table[bestKey];
    }

    // Оценки для моделей, которых нет в таблицах 2022 года (порядок важен: длинные названия раньше коротких).
    private static readonly (string Pattern, double Score)[] NewGpuEstimates =
    {
        (@"rtx\s*5090", 39000), (@"rtx\s*5080", 36000), (@"rtx\s*5070\s*ti", 34000), (@"rtx\s*5070", 31000),
        (@"rtx\s*5060\s*ti", 26500), (@"rtx\s*5060", 23000),
        (@"rtx\s*4090", 38000), (@"rtx\s*4080\s*super", 35500), (@"rtx\s*4080", 34800),
        (@"rtx\s*4070\s*ti\s*super", 34000), (@"rtx\s*4070\s*ti", 31500), (@"rtx\s*4070\s*super", 30000), (@"rtx\s*4070", 27000),
        (@"rtx\s*4060\s*ti", 22800), (@"rtx\s*4060", 19700), (@"rtx\s*4050", 15000),
        (@"rx\s*9070\s*xt", 33000), (@"rx\s*9070", 30000), (@"rx\s*9060\s*xt", 25000),
        (@"rx\s*7900\s*xtx", 31000), (@"rx\s*7900\s*xt", 28500), (@"rx\s*7900\s*gre", 26500), (@"rx\s*7800\s*xt", 25500),
        (@"rx\s*7700\s*xt", 22500), (@"rx\s*7600\s*xt", 18000), (@"rx\s*7600", 16500),
        (@"arc\s*b580", 20000), (@"arc\s*b570", 18000), (@"arc\s*a770", 16000), (@"arc\s*a750", 15000), (@"arc\s*a580", 13000),
    };

    internal static double? EstimateGpu(string name)
    {
        foreach (var (pattern, score) in NewGpuEstimates)
            if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase))
                return name.Contains("laptop", StringComparison.OrdinalIgnoreCase) || name.Contains("mobile", StringComparison.OrdinalIgnoreCase)
                    ? score * 0.7
                    : score;
        return null;
    }

    // ---------- Загрузка и кэш ----------

    private static async Task<string?> LoadCsvAsync(string cacheName, string[] urls, CancellationToken ct)
    {
        var path = Path.Combine(AppPaths.DataDirectory, "benchmarks", cacheName);
        try
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(30))
                return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (IOException) { /* читаем из сети */ }

        foreach (var url in urls)
        {
            try
            {
                var text = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
                if (text.Length < 1000) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
                return text;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                if (ct.IsCancellationRequested) throw;
                Logger.Error($"Не удалось загрузить таблицу производительности: {url}", ex);
            }
        }

        // Сеть недоступна: берём устаревший кэш, если он есть.
        try { return File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null; }
        catch (IOException) { return null; }
    }
}
