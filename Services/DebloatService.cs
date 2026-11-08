using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public enum AppKind
{
    /// <summary>Приложение Microsoft Store (UWP / MSIX).</summary>
    Store,

    /// <summary>Классическая программа из списка «Программы и компоненты».</summary>
    Win32,
}

public sealed record DebloatApp(
    string Id,
    string DisplayName,
    string PackageName,
    string PackageFullName,
    string Description,
    bool IsInstalled,
    bool IsRecommended,
    string? InstallLocation,
    string? IconPath,
    AppKind Kind = AppKind.Store,
    string Publisher = "",
    string Version = "",
    string? UninstallCommand = null,
    string? QuietUninstallCommand = null,
    string? RegistryKeyPath = null,
    string? PackageFamilyName = null,
    long SizeKb = 0,
    bool IsMsi = false,
    string? Launcher = null,
    string? LauncherGameId = null);

public interface IDebloatService
{
    Task<IReadOnlyList<DebloatApp>> ListAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> RemoveAsync(DebloatApp app, CancellationToken ct = default);

    /// <summary>Принудительное удаление, если штатный деинсталлятор не справился (Win32).</summary>
    Task<(bool Ok, string Message)> ForceRemoveAsync(DebloatApp app, CancellationToken ct = default);

    /// <summary>Остатки после удаления: папки, ярлыки, ключи реестра.</summary>
    Task<IReadOnlyList<Leftover>> FindLeftoversAsync(DebloatApp app, CancellationToken ct = default);

    Task<(int Removed, IReadOnlyList<string> Failed)> RemoveLeftoversAsync(IEnumerable<Leftover> items, CancellationToken ct = default);
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

    private static readonly HashSet<string> ProtectedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
        "Microsoft.SecHealthUI", "Microsoft.WindowsTerminal", "Microsoft.Windows.Search",
        "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.LockApp",
    };

    public async Task<IReadOnlyList<DebloatApp>> ListAsync(CancellationToken ct = default)
    {
        var packagesTask = Task.Run(GetInstalledPackages, ct);
        var win32Task = Task.Run(Win32Uninstaller.List, ct);
        var packages = await packagesTask.ConfigureAwait(false);
        var list = new List<DebloatApp>();

        foreach (var pkg in packages)
        {
            // Магазин, установщик приложений и компоненты безопасности не предлагаем к удалению
            if (ProtectedPackages.Contains(pkg.Name) ||
                pkg.Name.StartsWith("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase) ||
                pkg.Name.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase))
                continue;

            var catalog = Catalog.FirstOrDefault(c =>
                pkg.Name.Equals(c.Pattern, StringComparison.OrdinalIgnoreCase) ||
                pkg.Name.Contains(c.Pattern, StringComparison.OrdinalIgnoreCase));
            var known = catalog.Id is not null;

            var name = known && (string.IsNullOrWhiteSpace(pkg.DisplayName) || pkg.DisplayName == pkg.Name)
                ? catalog.Name
                : pkg.DisplayName;
            var desc = known
                ? catalog.Desc
                : (IsMicrosoftName(pkg.Name) ? "Приложение Microsoft" : "Приложение из Microsoft Store")
                  + (string.IsNullOrEmpty(pkg.Version) ? "" : " · версия " + pkg.Version);

            list.Add(new DebloatApp(
                known ? catalog.Id! : "store:" + pkg.Name,
                name,
                pkg.Name,
                pkg.PackageFullName,
                desc,
                IsInstalled: true,
                IsRecommended: known && catalog.Recommended,
                pkg.InstallLocation,
                ResolvePackageIcon(pkg.InstallLocation),
                AppKind.Store,
                Version: pkg.Version,
                Publisher: pkg.Publisher,
                PackageFamilyName: pkg.Family));
        }

        list.AddRange(await win32Task.ConfigureAwait(false));

        return list
            .OrderByDescending(a => a.IsRecommended)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<(bool Ok, string Message)> ForceRemoveAsync(DebloatApp app, CancellationToken ct = default) =>
        Task.Run(() => app.Launcher != null
            ? (false, $"Игру нужно удалять через {app.Launcher}: принудительное удаление отключено, чтобы не повредить лаунчер.")
            : app.Kind == AppKind.Win32
            ? Win32Uninstaller.ForceRemove(app)
            : (false, "Принудительное удаление доступно только для классических программ."), ct);

    public Task<IReadOnlyList<Leftover>> FindLeftoversAsync(DebloatApp app, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<Leftover>>(() => Win32Uninstaller.FindLeftovers(app), ct);

    public Task<(int Removed, IReadOnlyList<string> Failed)> RemoveLeftoversAsync(
        IEnumerable<Leftover> items, CancellationToken ct = default) =>
        Task.Run<(int, IReadOnlyList<string>)>(() =>
        {
            var (removed, failed) = Win32Uninstaller.RemoveLeftovers(items);
            return (removed, failed);
        }, ct);

    public async Task<(bool Ok, string Message)> RemoveAsync(DebloatApp app, CancellationToken ct = default)
    {
        try
        {
            // Игры из Steam и других лаунчеров удаляет только сам лаунчер: иначе можно снести и его папку.
            if (app.Launcher != null)
                return Win32Uninstaller.UninstallViaLauncher(app);

            if (app.Kind == AppKind.Win32)
            {
                var r = await Win32Uninstaller.UninstallAsync(app, ct).ConfigureAwait(false);
                if (r.Ok) Logger.Info($"Удалена программа: {app.DisplayName}");
                return r;
            }

            var pattern = string.IsNullOrWhiteSpace(app.PackageName) ? app.Id : app.PackageName;
            var script =
                $"Get-AppxPackage -Name '{Escape(pattern)}' | Remove-AppxPackage -ErrorAction SilentlyContinue; " +
                $"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -eq '{Escape(pattern)}' }} | " +
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

    private sealed record InstalledPackage(string Name, string PackageFullName, string InstallLocation, string DisplayName, string Family, string Version, string Publisher);

    private static List<InstalledPackage> GetInstalledPackages()
    {
        // CSV-подобная выдача: Name|FullName|InstallLocation
        const string script =
            "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } | " +
            "ForEach-Object { \"$($_.Name)|$($_.PackageFullName)|$($_.InstallLocation)|$($_.PackageFamilyName)|$($_.Version)|$($_.Publisher)\" }";
        var result = RunPowerShell(script);
        var list = new List<InstalledPackage>();

        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 3) continue;
            var name = parts[0].Trim();
            var full = parts[1].Trim();
            var loc = parts[2].Trim();
            var family = parts.Length > 3 ? parts[3].Trim() : "";
            var version = parts.Length > 4 ? parts[4].Trim() : "";
            var publisher = parts.Length > 5 ? PublisherFromSubject(string.Join("|", parts.Skip(5))) : "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(loc)) continue;

            var display = ReadDisplayName(loc) ?? FriendlyName(name);
            if (publisher.Length == 0 && IsMicrosoftName(name)) publisher = "Microsoft Corporation";
            list.Add(new InstalledPackage(name, full, loc, display, family, version, publisher));
        }

        return list;
    }

    private static bool IsMicrosoftName(string n) =>
        n.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase);

    private static string PublisherFromSubject(string subject)
    {
        var m = Regex.Match(subject, @"CN=(?:""([^""]+)""|([^,]+))");
        var cn = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim() : "";
        return cn;
    }

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.WindowsCalculator"] = "Калькулятор",
        ["Microsoft.WindowsCamera"] = "Камера",
        ["Microsoft.WindowsAlarms"] = "Будильники и часы",
        ["Microsoft.WindowsMaps"] = "Карты",
        ["Microsoft.WindowsNotepad"] = "Блокнот",
        ["Microsoft.WindowsTerminal"] = "Терминал Windows",
        ["Microsoft.WindowsSoundRecorder"] = "Запись голоса",
        ["Microsoft.WindowsFeedbackHub"] = "Центр отзывов",
        ["Microsoft.WindowsCommunicationsApps"] = "Почта и Календарь",
        ["Microsoft.Paint"] = "Paint",
        ["Microsoft.ScreenSketch"] = "Набросок на фрагменте экрана",
        ["Microsoft.Windows.Photos"] = "Фотографии",
        ["Microsoft.Photos"] = "Фотографии",
        ["Microsoft.ZuneMusic"] = "Медиаплеер",
        ["Microsoft.ZuneVideo"] = "Кино и ТВ",
        ["Microsoft.People"] = "Люди",
        ["Microsoft.MicrosoftStickyNotes"] = "Записки",
        ["Microsoft.MicrosoftSolitaireCollection"] = "Microsoft Solitaire Collection",
        ["Microsoft.MicrosoftOfficeHub"] = "Microsoft 365 (Office)",
        ["Microsoft.Office.OneNote"] = "OneNote",
        ["Microsoft.OutlookForWindows"] = "Outlook (новый)",
        ["Microsoft.Todos"] = "Microsoft To Do",
        ["Microsoft.BingWeather"] = "Погода",
        ["Microsoft.BingNews"] = "Новости",
        ["Microsoft.BingSearch"] = "Поиск Bing",
        ["Microsoft.GetHelp"] = "Техническая поддержка",
        ["Microsoft.Getstarted"] = "Советы",
        ["Microsoft.Copilot"] = "Copilot",
        ["Microsoft.549981C3F5F10"] = "Cortana",
        ["Microsoft.YourPhone"] = "Связь с телефоном",
        ["Microsoft.MixedReality.Portal"] = "Портал смешанной реальности",
        ["Microsoft.Microsoft3DViewer"] = "Просмотр 3D",
        ["Microsoft.Print3D"] = "Print 3D",
        ["Microsoft.3DBuilder"] = "3D Builder",
        ["Microsoft.SkypeApp"] = "Skype",
        ["Microsoft.Whiteboard"] = "Microsoft Whiteboard",
        ["Microsoft.PowerAutomateDesktop"] = "Power Automate",
        ["Microsoft.Teams"] = "Microsoft Teams",
        ["MicrosoftTeams"] = "Microsoft Teams",
        ["Microsoft.OneDriveSync"] = "OneDrive",
        ["Microsoft.XboxApp"] = "Xbox",
        ["Microsoft.GamingApp"] = "Xbox",
        ["Microsoft.XboxGamingOverlay"] = "Xbox Game Bar",
        ["Microsoft.XboxGameOverlay"] = "Xbox Game Overlay",
        ["Microsoft.XboxIdentityProvider"] = "Поставщик удостоверений Xbox",
        ["Microsoft.XboxSpeechToTextOverlay"] = "Xbox Speech To Text",
        ["Microsoft.Xbox.TCUI"] = "Xbox TCUI",
        ["Microsoft.DevHome"] = "Dev Home",
        ["Microsoft.Edge.GameAssist"] = "Edge Game Assist",
        ["Microsoft.MicrosoftEdge.Stable"] = "Microsoft Edge",
        ["MicrosoftCorporationII.QuickAssist"] = "Быстрая помощь",
        ["MicrosoftCorporationII.MicrosoftFamily"] = "Семья Microsoft",
        ["Microsoft.StartExperiencesApp"] = "Начальные возможности Windows",
        ["Microsoft.ApplicationCompatibilityEnhancements"] = "Улучшения совместимости приложений",
        ["Microsoft.WidgetsPlatformRuntime"] = "Платформа виджетов",
        ["MicrosoftWindows.Client.WebExperience"] = "Виджеты Windows",
        ["Microsoft.MSPaint"] = "Paint 3D",
    };

    private static string FriendlyName(string packageName)
    {
        if (FriendlyNames.TryGetValue(packageName, out var n)) return n;

        // Языковые пакеты: Microsoft.LanguageExperiencePackru-RU
        var lang = Regex.Match(packageName, @"LanguageExperiencePack([a-z]{2,3}(?:-[A-Za-z0-9]+)*)$", RegexOptions.IgnoreCase);
        if (lang.Success)
        {
            try { return "Языковой пакет: " + System.Globalization.CultureInfo.GetCultureInfo(lang.Groups[1].Value).DisplayName; }
            catch { return "Языковой пакет " + lang.Groups[1].Value; }
        }

        var s = packageName;
        foreach (var prefix in new[] { "MicrosoftCorporationII.", "MicrosoftWindows.", "Microsoft.Windows.", "Microsoft.", "Windows." })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..]; break; }
        s = Regex.Replace(s, @"(?<=[a-z0-9])(?=[A-Z])", " ").Replace('.', ' ').Trim();
        return s.Length == 0 ? packageName : s;
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
