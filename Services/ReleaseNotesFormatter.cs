using System.Text;
using System.Text.RegularExpressions;

namespace WinOptimizer.Services;

/// <summary>
/// Превращает описание релиза GitHub (по умолчанию — текст последнего коммита) в короткий список для окна обновления.
/// </summary>
public static class ReleaseNotesFormatter
{
    private static readonly Regex Bullet = new(@"^(?:[-*+•]|\d+[.)])\s+", RegexOptions.Compiled);
    private static readonly Regex CommitPrefix = new(
        @"^(?<type>feat|fix|perf|refactor|docs|chore|ci|test|style|build|ui)(?:\([^)]*\))?!?:\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex PullRef = new(@"\s*\(#\d+\)\s*$", RegexOptions.Compiled);

    private static readonly string[] NoisePrefixes =
    {
        "co-authored-by:", "claude-session:", "signed-off-by:", "full changelog", "**full changelog",
        "generated with", "🤖", "http://", "https://", "#",
    };

    /// <summary>До <paramref name="maxItems"/> пунктов «• …»; пустая строка, если описания нет.</summary>
    public static string Summarize(string? notes, int maxItems = 6, int maxItemLength = 110)
    {
        if (string.IsNullOrWhiteSpace(notes) || maxItems <= 0) return string.Empty;

        var items = new List<string>();
        var truncated = false;
        foreach (var raw in notes.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || IsNoise(line)) continue;

            line = Bullet.Replace(line, "");
            line = Humanize(line);
            if (line.Length == 0) continue;
            if (items.Contains(line, StringComparer.OrdinalIgnoreCase)) continue;

            if (items.Count == maxItems) { truncated = true; break; }
            items.Add(line.Length > maxItemLength ? line[..(maxItemLength - 1)].TrimEnd() + "…" : line);
        }

        if (items.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendJoin('\n', items.Select(i => "• " + i));
        if (truncated) sb.Append("\n• …и другие изменения");
        return sb.ToString();
    }

    private static bool IsNoise(string line) =>
        NoisePrefixes.Any(p => line.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string Humanize(string line)
    {
        line = Link.Replace(line, "$1").Replace("**", "").Replace("`", "");
        line = PullRef.Replace(line, "");

        var m = CommitPrefix.Match(line);
        if (m.Success)
        {
            var rest = line[m.Length..].Trim();
            if (rest.Length == 0) return string.Empty;
            var label = m.Groups["type"].Value.ToLowerInvariant() switch
            {
                "fix" => "Исправлено: ",
                "feat" => "Новое: ",
                "perf" => "Быстрее: ",
                _ => "",
            };
            line = label.Length > 0 ? label + rest : char.ToUpperInvariant(rest[0]) + rest[1..];
        }

        line = line.Trim();
        return line.Length == 0 ? line : char.ToUpperInvariant(line[0]) + line[1..];
    }
}
