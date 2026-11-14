using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WinOptimizer.Services;

public sealed record PowerPlanInfo(string Id, string Name, bool IsActive);

/// <summary>Reads the power plans that Windows currently exposes through powercfg.</summary>
public static class PowerPlanCatalog
{
    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> KnownNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["381b4222-f694-41f0-9685-ff5bb260df2e"] = "Сбалансированный",
            ["a1841308-3541-4fab-bc81-f71556f20b4a"] = "Экономия энергии",
            ["8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"] = "Высокая производительность",
            ["e9a42b02-d5df-448d-aa00-03f14749eb61"] = "Максимальная производительность"
        };

    public static IReadOnlyList<PowerPlanInfo> GetAvailablePlans()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/list");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить powercfg.exe.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Получение списка планов питания превысило 5 секунд.");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "powercfg.exe /list завершился с ошибкой: " + (string.IsNullOrWhiteSpace(error) ? output : error));

        return Parse(output);
    }

    public static IReadOnlyList<PowerPlanInfo> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return Array.Empty<PowerPlanInfo>();

        var plans = new Dictionary<string, PowerPlanInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(new[] { '\\r', '\\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = GuidPattern.Match(line);
            if (!match.Success || !Guid.TryParse(match.Value, out var guid))
                continue;

            var id = guid.ToString("D");
            var tail = line[(match.Index + match.Length)..].Trim();
            var nameMatch = Regex.Match(tail, @"\\((?<name>[^)]*)\\)");
            var parsedName = nameMatch.Success ? nameMatch.Groups["name"].Value.Trim() : "";
            var name = KnownNames.TryGetValue(id, out var knownName)
                ? knownName
                : !string.IsNullOrWhiteSpace(parsedName)
                    ? parsedName
                    : "План " + id[..8];
            var isActive = tail.Contains('*');

            plans[id] = new PowerPlanInfo(id, name, isActive);
        }

        return plans.Values
            .OrderByDescending(plan => plan.IsActive)
            .ThenBy(plan => plan.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
