using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public sealed record DebloatApp(
    string Id,
    string DisplayName,
    string PackageName,
    string PackageFullName,
    string Description,
    bool IsInstalled,
    bool IsRecommended,
    string? InstallLocation,
    string? IconPath);

public interface IDebloatService
{
    Task<IReadOnlyList<DebloatApp>> ListAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> RemoveAsync(DebloatApp app, CancellationToken ct = default);
}

/// <summary>
/// Только реально установленные UWP-пакеты из белого списка.
/// Иконки берутся из AppxManifest / Assets пакета Windows.
/// </summary>
public sealed class DebloatService : IDebloatService
{
    private static readonly (string Id, string Name, string Pattern, string Desc, bool Recommended)[] Catalog =
    [
        ("clipchamp", "Clipchamp", "Clipchamp.Clipchamp", "Видеоредактор Microsoft", true),
        ("bing-news", "Новости (Bing)", "Microsoft.BingNews", "Виджеты и лента новостей", true),
        ("bing-weather", "Погода", "Microsoft.BingWeather", "Приложение «Погода»", true),
        ("bing-finance", "Финансы", "Microsoft.BingFinance", "Приложение «Финансы»", true),
        ("bing-sports", "Спорт", "Microsoft.BingSports", "Приложение «Спорт»", true),
        ("get-help", "Получить помощь", "Microsoft.GetHelp", "Справка Microsoft", true),
        ("get-started", "Советы", "Microsoft.Getstarted", "Приложение «Советы»", true),
        ("messaging", "Сообщения", "Microsoft.Messaging", "SMS/сообщения", true),
        ("mixed-reality", "Mixed Reality Portal", "Microsoft.MixedReality.Portal", "Портал смешанной реальности", true),
        ("office-hub", "Office Hub", "Microsoft.MicrosoftOfficeHub", "Реклама Office", true),
        ("solitaire", "Microsoft Solitaire", "Microsoft.MicrosoftSolitaireCollection", "Коллекция пасьянсов", true),
        ("sticky-notes", "Sticky Notes", "Microsoft.MicrosoftStickyNotes", "Заметки", false),
        ("people", "Люди", "Microsoft.People", "Контакты", true),
        ("skype", "Skype", "Microsoft.SkypeApp", "Skype", true),
        ("todos", "Microsoft To Do", "Microsoft.Todos", "Задачи", false),
        ("windows-maps", "Карты", "Microsoft.WindowsMaps", "Карты Windows", true),
        ("windows-sound", "Звукозапись", "Microsoft.WindowsSoundRecorder", "Диктофон", false),
        ("xbox-app", "Xbox", "Microsoft.XboxApp", "Классическое приложение Xbox", true),
        ("xbox-gaming-overlay", "Xbox Game Bar", "Microsoft.XboxGamingOverlay", "Оверлей Game Bar", false),
        ("xbox-identity", "Xbox Identity Provider", "Microsoft.XboxIdentityProvider", "Идентификация Xbox", false),
        ("xbox-speech", "Xbox Speech To Text", "Microsoft.XboxSpeechToTextOverlay", "Оверлей речи Xbox", true),
        ("xbox-tcui", "Xbox TCUI", "Microsoft.Xbox.TCUI", "Xbox UI", false),
        ("gaming-app", "Xbox Gaming App", "Microsoft.GamingApp", "Xbox (новое)", true),
        ("your-phone", "Связь с телефоном", "Microsoft.YourPhone", "Phone Link", false),
        ("zune-music", "Groove Music", "Microsoft.ZuneMusic", "Музыка Groove", true),
        ("zune-video", "Кино и ТВ", "Microsoft.ZuneVideo", "Кино и ТВ", true),
        ("onedrive-uwp", "OneDrive (UWP)", "Microsoft.OneDriveSync", "OneDrive UWP-часть", false),
        ("copilot", "Copilot", "Microsoft.Copilot", "Copilot", true),
        ("teams", "Microsoft Teams (consumer)", "MicrosoftTeams", "Teams для потребителей", true),
        ("feedback-hub", "Центр отзывов", "Microsoft.WindowsFeedbackHub", "Feedback Hub", true),
        ("cortana", "Cortana", "Microsoft.549981C3F5F10", "Cortana", true),
        ("power-automate", "Power Automate", "Microsoft.PowerAutomateDesktop", "Power Automate", true),
        ("dev-home", "Dev Home", "Microsoft.Windows.DevHome", "Dev Home", true),
        ("quick-assist", "Быстрая помощь", "MicrosoftCorporationII.QuickAssist", "Quick Assist", false),
    ];

    public async Task<IReadOnlyList<DebloatApp>> ListAsync(CancellationToken ct = default)
    {
        var packages = await Task.Run(GetInstalledPackages, ct).ConfigureAwait(false);
        var list = new List<DebloatApp>();

        foreach (var (id, name, pattern, desc, recommended) in Catalog)
        {
            var match = packages.FirstOrDefault(p =>
                p.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                pattern.Contains(p.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                continue;

            var icon = ResolvePackageIcon(match.InstallLocation);
            list.Add(new DebloatApp(
                id,
                string.IsNullOrWhiteSpace(match.DisplayName) ? name : match.DisplayName,
                match.Name,
                match.PackageFullName,
                desc,
                IsInstalled: true,
                recommended,
                match.InstallLocation,
                icon));
        }

        return list.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<(bool Ok, string Message)> RemoveAsync(DebloatApp app, CancellationToken ct = default)
    {
        try
        {
            var pattern = string.IsNullOrWhiteSpace(app.PackageName) ? app.Id : app.PackageName;
            var script =
                $"Get-AppxPackage -Name '*{Escape(pattern)}*' | Remove-AppxPackage -ErrorAction SilentlyContinue; " +
                $"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -like '*{Escape(pattern)}*' }} | " +
                "Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue; " +
                "'OK'";
            var result = await Task.Run(() => RunPowerShell(script), ct).ConfigureAwait(false);
            if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                Logger.Error($"Debloat remove failed: {app.DisplayName} {result.StandardError}");
                return (false, string.IsNullOrWhiteSpace(result.StandardError) ? "Не удалось удалить пакет." : result.StandardError.Trim());
            }

            Logger.Info($"Debloat: удалён {app.DisplayName}");
            return (true, "Удалено");
        }
        catch (Exception ex)
        {
            Logger.Error($"Debloat exception: {app.DisplayName}", ex);
            return (false, ex.Message);
        }
    }

    private sealed record InstalledPackage(string Name, string PackageFullName, string InstallLocation, string DisplayName);

    private static List<InstalledPackage> GetInstalledPackages()
    {
        // CSV-подобная выдача: Name|FullName|InstallLocation
        const string script =
            "Get-AppxPackage | ForEach-Object { \"$($_.Name)|$($_.PackageFullName)|$($_.InstallLocation)\" }";
        var result = RunPowerShell(script);
        var list = new List<InstalledPackage>();

        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 3) continue;
            var name = parts[0].Trim();
            var full = parts[1].Trim();
            var loc = parts[2].Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(loc)) continue;

            var display = ReadDisplayName(loc) ?? name;
            list.Add(new InstalledPackage(name, full, loc, display));
        }

        return list;
    }

    private static string? ReadDisplayName(string installLocation)
    {
        try
        {
            var manifest = Path.Combine(installLocation, "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;
            var doc = XDocument.Load(manifest);
            XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
            XNamespace def = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            var display = doc.Descendants(uap + "VisualElements").Attributes("DisplayName").FirstOrDefault()?.Value
                          ?? doc.Descendants().Attributes("DisplayName").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(display) || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                return null;
            return display;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ищем логотип пакета: AppxManifest Logo / Square44x44Logo / StoreLogo, затем scale-варианты.
    /// </summary>
    public static string? ResolvePackageIcon(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            return null;

        try
        {
            var manifestPath = Path.Combine(installLocation, "AppxManifest.xml");
            var candidates = new List<string>();

            if (File.Exists(manifestPath))
            {
                var text = File.ReadAllText(manifestPath);
                foreach (Match m in Regex.Matches(text,
                             """(?i)(?:Square44x44Logo|Square150x150Logo|StoreLogo|Logo|AppListEntry)=["']([^"']+)["']"""))
                {
                    var rel = m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
                    if (rel.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase)) continue;
                    candidates.Add(rel);
                }
            }

            // Типичные имена ассетов
            candidates.AddRange([
                @"Assets\StoreLogo.png",
                @"Assets\Square44x44Logo.png",
                @"Assets\Square150x150Logo.png",
                @"Assets\AppList.png",
                @"Assets\Logo.png",
                @"StoreLogo.png",
                @"Square44x44Logo.png",
            ]);

            foreach (var rel in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var found = FindScaledAsset(installLocation, rel);
                if (found != null) return found;
            }

            // Любой png в Assets с Logo в имени
            var assets = Path.Combine(installLocation, "Assets");
            if (Directory.Exists(assets))
            {
                var hit = Directory.EnumerateFiles(assets, "*.png", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var n = Path.GetFileName(f);
                        return n.Contains("Logo", StringComparison.OrdinalIgnoreCase)
                               || n.Contains("AppList", StringComparison.OrdinalIgnoreCase)
                               || n.Contains("Store", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(f => f.Contains("scale-200", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.Contains("44", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ResolvePackageIcon", ex);
        }

        return null;
    }

    private static string? FindScaledAsset(string root, string relative)
    {
        var direct = Path.Combine(root, relative);
        if (File.Exists(direct)) return direct;

        var dir = Path.GetDirectoryName(Path.Combine(root, relative)) ?? root;
        var baseName = Path.GetFileNameWithoutExtension(relative);
        var ext = Path.GetExtension(relative);
        if (!Directory.Exists(dir)) return null;

        // StoreLogo.scale-200.png, StoreLogo.targetsize-44.png и т.п.
        var match = Directory.EnumerateFiles(dir, baseName + "*" + ext)
            .OrderByDescending(f => f.Contains("scale-200", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.Contains("scale-100", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.Contains("targetsize-44", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        return match;
    }

    private static ProcessResult RunPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        return ProcessRunner.Run("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            timeoutMs: 120_000);
    }

    private static string Escape(string s) => s.Replace("'", "''");
}
