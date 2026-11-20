using System.Xml.Linq;
using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

/// <summary>
/// Structural regression tests for the collapsible Fluent UI sections.
/// These tests do not replace visual verification on Windows, but catch accidental
/// removal of the sections or changes to their intended initial state.
/// </summary>
public sealed class UiStructureTests
{
    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinOptimizer.csproj")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Не удалось найти корень репозитория.");
    }

    internal static XDocument LoadView(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), relativePath));

    private static IEnumerable<XElement> Elements(XDocument document, string name) =>
        document.Descendants().Where(element => element.Name.LocalName == name);

    private static IEnumerable<XElement> Elements(XElement element, string name) =>
        element.Descendants().Where(child => child.Name.LocalName == name);

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    [Fact]
    public void MainWindowXaml_DoesNotContainEscapedNewlineTextNodes()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Views/MainWindow.xaml"));
        Assert.DoesNotContain("\\n", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainShell_UsesGraphiteControlCenterVisualLanguage()
    {
        var view = LoadView("Views/MainWindow.xaml");
        var theme = LoadView("Themes/DarkTheme.xaml");

        Assert.Equal("{DynamicResource BgWindowBrush}", Attribute(view.Root!, "Background"));
        Assert.Contains(Elements(view, "Rectangle"), element =>
            Attribute(element, "Name") == "BackgroundDim" &&
            Attribute(element, "Fill") == "{DynamicResource BgWindowBrush}" &&
            Attribute(element, "Visibility") == "Collapsed");
        Assert.DoesNotContain(Elements(view, "Rectangle"), element =>
            Attribute(element, "Fill") == "{DynamicResource SiteGridBrush}");
        Assert.DoesNotContain(Elements(view, "Ellipse"), element =>
            (string?)element.Attribute("Fill") is "{DynamicResource SiteCyanGlowBrush}" or
                "{DynamicResource SiteBlueGlowBrush}");

        foreach (var (key, value) in new[]
        {
            ("BgWindowColor", "#0D1117"),
            ("BgSidebarColor", "#010409"),
            ("BgCardColor", "#161B22"),
            ("AccentColor", "#3FB950")
        })
        {
            Assert.Contains(Elements(theme, "Color"), element =>
                Attribute(element, "Key") == key && element.Value.Trim() == value);
        }

        var accentButton = Assert.Single(Elements(theme, "Style"), element =>
            Attribute(element, "Key") == "AccentButton");
        Assert.Contains(Elements(accentButton, "Setter"), element =>
            Attribute(element, "Property") == "Background" &&
            Attribute(element, "Value") == "{DynamicResource SiteAccentButtonBrush}");
        Assert.Contains(Elements(accentButton, "Setter"), element =>
            Attribute(element, "Property") == "BorderBrush" &&
            Attribute(element, "Value") == "{DynamicResource SiteAccentButtonBrush}");
    }

    [Fact]
    public void MainShell_UsesTerminalDesktopChrome()
    {
        var view = LoadView("Views/MainWindow.xaml");
        var root = Assert.Single(Elements(view, "Grid"), element =>
            Attribute(element, "Name") == "RootGrid");
        var rows = Elements(root, "RowDefinition").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("54", Attribute(rows[0], "Height"));
        Assert.Equal("*", Attribute(rows[1], "Height"));

        Assert.Contains(Elements(root, "Border"), element =>
            Attribute(element, "Row") == "0" &&
            Attribute(element, "ColumnSpan") == "2" &&
            Attribute(element, "Background") == "#010409" &&
            Attribute(element, "BorderThickness") == "0,0,0,1");
        Assert.Contains(Elements(root, "TextBlock"), element =>
            Attribute(element, "Text") == "LOCAL SESSION");
        Assert.Contains(Elements(root, "TextBlock"), element =>
            Attribute(element, "Text") == "SYSTEM CONTROL");
        Assert.Contains(Elements(root, "Border"), element =>
            Attribute(element, "Name") == "SidebarPanel" &&
            Attribute(element, "Row") == "1");
        Assert.Contains(Elements(root, "Grid"), element =>
            Attribute(element, "Row") == "1" &&
            Attribute(element, "Column") == "1");

        var theme = LoadView("Themes/DarkTheme.xaml");
        var nav = Assert.Single(Elements(theme, "Style"), element =>
            Attribute(element, "Key") == "NavButton");
        Assert.Contains(Elements(nav, "Border"), element =>
            Attribute(element, "Name") == "Root" &&
            Attribute(element, "CornerRadius") == "0");
    }

    [Fact]
    public void MainShell_UsesTerminalTypographyWithoutBecomingAConsole()
    {
        var view = LoadView("Views/MainWindow.xaml");
        Assert.Equal("Cascadia Mono, Cascadia Code, Consolas", Attribute(view.Root!, "FontFamily"));
        Assert.Equal("13", Attribute(view.Root!, "FontSize"));
        Assert.Equal("WinOptimizer", Attribute(view.Root!, "Title"));
    }

    [Fact]
    public void CardSystem_UsesTechnicalPanelGeometry()
    {
        var main = LoadView("Views/MainWindow.xaml");
        var extras = LoadView("Themes/DarkTheme.Extras.xaml");
        var shared = LoadView("Themes/SharedStyles.xaml");
        var dashboardCard = Assert.Single(Elements(main, "Style"), element =>
            Attribute(element, "Key") == "DashboardCard");
        Assert.Contains(Elements(dashboardCard, "Setter"), element =>
            Attribute(element, "Property") == "CornerRadius" &&
            Attribute(element, "Value") == "0");
        Assert.Contains(Elements(dashboardCard, "Setter"), element =>
            Attribute(element, "Property") == "Effect" &&
            Attribute(element, "Value") == "{x:Null}");

        var hoverCard = Assert.Single(Elements(extras, "Style"), element =>
            Attribute(element, "Key") == "HoverCard");
        Assert.Contains(Elements(hoverCard, "Border"), element =>
            Attribute(element, "Name") == "StateRail");

        foreach (var document in new[] { extras, shared })
        {
            var borderCard = Assert.Single(Elements(document, "Style"), element =>
                Attribute(element, "Key") == "BorderCard");
            Assert.Contains(Elements(borderCard, "Setter"), element =>
                Attribute(element, "Property") == "CornerRadius" &&
                Attribute(element, "Value") == "0");
        }
    }

    [Fact]
    public void GameAndUtilityPanels_StayWithinDesignSystemRadius()
    {
        var main = LoadView("Views/MainWindow.xaml");
        var gamePage = Assert.Single(Elements(main, "Grid"), element =>
            Attribute(element, "Visibility") == "{Binding IsGamesPage, Converter={StaticResource BoolToVis}}");

        Assert.Empty(Elements(gamePage, "Border").Where(element =>
            double.TryParse(Attribute(element, "CornerRadius"), out var radius) && radius > 16));

        var utilityPages = LoadView("Views/MainWindowPages.xaml");
        Assert.Empty(Elements(utilityPages, "Border").Where(element =>
            double.TryParse(Attribute(element, "CornerRadius"), out var radius) && radius > 16));
    }

    [Fact]
    public void DashboardHero_UsesControlCenterStatusPanelInsteadOfDecorativeBanner()
    {
        var view = LoadView("Views/MainWindow.xaml");
        Assert.Contains(Elements(view, "TextBlock"), element =>
            Attribute(element, "Text") == "ROOT / SYSTEM STATUS");
        Assert.Contains(Elements(view, "TextBlock"), element =>
            Attribute(element, "Text") == "Состояние системы");
        Assert.Contains(Elements(view, "Button"), element =>
            Attribute(element, "Content") == "Обновить данные");
    }

    [Fact]
    public void Dashboard_UsesUnifiedTelemetryAndDriveInventoryRows()
    {
        var view = LoadView("Views/MainWindow.xaml");
        Assert.Contains(Elements(view, "Border"), element =>
            Attribute(element, "Name") == "DashboardTelemetryPanel");
        Assert.Contains(Elements(view, "Grid"), element =>
            element.Descendants().Any(child => child.Name.LocalName == "TextBlock" &&
                Attribute(child, "Text") == "{Binding DisplayName}") &&
            element.Descendants().Any(child => child.Name.LocalName == "ProgressBar" &&
                Attribute(child, "Maximum") == "100"));
        Assert.DoesNotContain(Elements(view, "Border"), element =>
            Attribute(element, "Width") == "230" &&
            Attribute(element, "CornerRadius") == "12");
    }

    [Fact]
    public void SharedSurfaces_UseTheNewControlDeskTreatment()
    {
        var shared = LoadView("Themes/SharedStyles.xaml");
        var extras = LoadView("Themes/DarkTheme.Extras.xaml");

        var pageTitle = Assert.Single(Elements(shared, "Style"), element =>
            Attribute(element, "Key") == "PageTitle");
        Assert.Contains(Elements(pageTitle, "Setter"), element =>
            Attribute(element, "Property") == "FontSize" &&
            Attribute(element, "Value") == "29");

        var borderCard = Assert.Single(Elements(shared, "Style"), element =>
            Attribute(element, "Key") == "BorderCard");
        Assert.Contains(Elements(borderCard, "Setter"), element =>
            Attribute(element, "Property") == "CornerRadius" &&
            Attribute(element, "Value") == "0");

        var hoverCard = Assert.Single(Elements(extras, "Style"), element =>
            Attribute(element, "Key") == "HoverCard");
        Assert.Contains(Elements(hoverCard, "Border"), element =>
            Attribute(element, "Name") == "Card" &&
            Attribute(element, "CornerRadius") == "0" &&
            Attribute(element, "BorderThickness") == "1");
    }

    [Fact]
    public void BrandMark_UsesTheSharedApplicationIconResource()
    {
        var document = LoadView("Views/MainWindow.xaml");
        var logoBorder = Assert.Single(Elements(document, "Border"), element =>
            Attribute(element, "Width") == "38" &&
            Attribute(element, "Height") == "38" &&
            element.Descendants().Any(child => child.Name.LocalName == "Image"));

        var image = Assert.Single(logoBorder.Descendants(), element =>
            element.Name.LocalName == "Image");
        Assert.Equal("{StaticResource WinOptimizerAppIcon}", Attribute(image, "Source"));
        Assert.Equal("Uniform", Attribute(image, "Stretch"));

        var app = LoadView("App.xaml");
        Assert.Contains(Elements(app, "DrawingImage"), element =>
            Attribute(element, "Key") == "WinOptimizerAppIcon" &&
            Elements(element, "GeometryDrawing").Any(drawing => Attribute(drawing, "Brush") == "#3FB950") &&
            Elements(element, "GeometryDrawing").Count() == 5);
    }

    [Fact]
    public void GithubBanner_BrandMarkCoordinatesStayInsideSquare()
    {
        var banner = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/banner.svg"));
        var document = XDocument.Parse(banner);
        var mark = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "g" &&
            (string?)element.Attribute("transform") == "translate(86 94)");
        var paths = mark.Elements().Where(element => element.Name.LocalName == "path").ToList();
        Assert.Equal(2, paths.Count);

        foreach (var path in paths)
        {
            var coordinates = System.Text.RegularExpressions.Regex.Matches(
                (string?)path.Attribute("d") ?? string.Empty,
                @"[-+]?(?:\d+\.?\d*|\.\d+)")
                .Select(match => double.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.All(coordinates, coordinate => Assert.InRange(coordinate, 0, 150));
        }
    }

    [Fact]
    public void MainShell_SupportsCompactAndExpandedSidebarModes()
    {
        var view = LoadView("Views/MainWindow.xaml");
        var root = Assert.Single(Elements(view, "Grid"), element =>
            Attribute(element, "Name") == "RootGrid");
        var column = Assert.Single(Elements(root, "ColumnDefinition"), element =>
            Attribute(element, "Name") == "SidebarColumn");
        Assert.Equal("240", Attribute(column, "Width"));

        var navigation = Elements(root, "RadioButton")
            .Where(element => Attribute(element, "ToolTip") is not null)
            .ToArray();
        Assert.Equal(8, navigation.Length);
        Assert.All(navigation, item =>
            Assert.Equal("{StaticResource SidebarNavigationButton}", Attribute(item, "Style")));

        foreach (var label in new[] { "Главная", "Оптимизация", "Приложения", "Игры",
                                      "Мой ПК", "Задержка и прерывания", "Обслуживание", "Настройки" })
        {
            Assert.Contains(Elements(root, "TextBlock"), element =>
                Attribute(element, "Text") == label &&
                Attribute(element, "Visibility")?.Contains("IsSidebarExpanded", StringComparison.Ordinal) == true);
        }

        Assert.Contains(Elements(view, "Style"), element =>
            Attribute(element, "Key") == "SidebarNavigationButton");
        Assert.Contains(Elements(view, "Style"), element =>
            Attribute(element, "Key") == "SidebarNavigationIcon");
        Assert.DoesNotContain(Elements(root, "Button"), element =>
            Attribute(element, "Style") == "{StaticResource SidebarHandleButton}");
    }

    [Fact]
    public void Installer_UsesCompactWizardAndPreservesExistingData()
    {
        var installer = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer/WinOptimizer.iss"));
        Assert.Contains("WizardSizePercent=100", installer);
        Assert.Contains("Your settings and tweak backups will be preserved.", installer);
        Assert.Contains("DelTree(DataDir, True, True, True)", installer);
        Assert.Contains("if DirExists(DataDir)", installer);
    }

    [Fact]
    public void MainShell_SidebarModeCanBeToggledAndPersisted()
    {
        var view = LoadView("Views/MainWindow.xaml");

        var column = Assert.Single(Elements(view, "ColumnDefinition"), element =>
            Attribute(element, "Name") == "SidebarColumn");
        Assert.Equal("240", Attribute(column, "Width"));
        Assert.Contains(Elements(view, "Border"), element => Attribute(element, "Name") == "SidebarPanel");

        Assert.Contains(Elements(view, "Button"), element =>
            Attribute(element, "Style") == "{StaticResource SidebarToggleButton}" &&
            Attribute(element, "Command") == "{Binding ToggleSidebarCommand}");

        var toggleStyle = Assert.Single(Elements(view, "Style"), element =>
            Attribute(element, "Key") == "SidebarToggleButton");
        Assert.Contains(Elements(toggleStyle, "Setter"), element =>
            Attribute(element, "Property") == "Content" &&
            Attribute(element, "Value") == "‹");
        Assert.Contains(Elements(toggleStyle, "DataTrigger"), element =>
            Attribute(element, "Value") == "False" &&
            Elements(element, "Setter").Any(setter =>
                Attribute(setter, "Property") == "Content" &&
                Attribute(setter, "Value") == "›"));
        Assert.Contains(Elements(view, "KeyBinding"), element =>
            Attribute(element, "Key") == "B" &&
            Attribute(element, "Modifiers") == "Control" &&
            Attribute(element, "Command") == "{Binding ToggleSidebarCommand}");
    }

    [Fact]
    public void Sidebar_UsesConsistentTechnicalOutlineIcons()
    {
        var document = LoadView("Views/MainWindow.xaml");
        var navigationIcons = Elements(document, "Path")
            .Where(element => Attribute(element, "Stroke") ==
                "{Binding Foreground, RelativeSource={RelativeSource AncestorType=RadioButton}}")
            .ToList();

        Assert.Equal(10, navigationIcons.Count);
        Assert.All(navigationIcons, icon =>
        {
            Assert.Equal("Transparent", Attribute(icon, "Fill"));
            Assert.Equal("1.7", Attribute(icon, "StrokeThickness"));
            Assert.Equal("Round", Attribute(icon, "StrokeLineJoin"));
        });

        var application = XDocument.Load(Path.Combine(RepositoryRoot(), "App.xaml"));
        Assert.Contains(Elements(application, "GeometryDrawing"), drawing =>
            Attribute(drawing, "Brush") == "#3FB950");
    }

    [Fact]
    public void SideMouseButtons_AreConnectedToNavigationHistory()
    {
        var codeBehind = File.ReadAllText(Path.Combine(RepositoryRoot(), "Views/MainWindow.xaml.cs"));

        Assert.Contains("private const int WmXButtonDown = 0x020B;", codeBehind);
        Assert.Contains("private const int XButton1 = 0x0001", codeBehind);
        Assert.Contains("private const int XButton2 = 0x0002", codeBehind);
        Assert.Contains("XButton1 => _viewModel.NavigateBack()", codeBehind);
        Assert.Contains("XButton2 => _viewModel.NavigateForward()", codeBehind);
        Assert.Contains("_windowSource?.AddHook(WindowProc)", codeBehind);
        Assert.Contains("_windowSource?.RemoveHook(WindowProc)", codeBehind);
    }

    [Fact]
    public void VersionAction_UsesInPlaceUpdateInsteadOfDownloadingSetup()
    {
        var document = LoadView("Views/MainWindow.xaml");

        Assert.Contains(Elements(document, "Button"), element =>
            Attribute(element, "Content") == "↻" &&
            Attribute(element, "Command") == "{Binding CheckUpdateCommand}" &&
            Attribute(element, "ToolTip") == "{Binding UpdateButtonToolTip}");

        Assert.DoesNotContain(Elements(document, "Button"), element =>
            Attribute(element, "Command") == "{Binding DownloadLatestVersionCommand}");
        Assert.DoesNotContain(Elements(document, "MenuItem"), element =>
            (Attribute(element, "Header") ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
            (Attribute(element, "Header") ?? "").Contains("(.exe)", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dashboard_ShowsCoreInformationWithoutCollapsedLists()
    {
        var document = LoadView("Views/MainWindow.xaml");
        var dashboard = Assert.Single(Elements(document, "Grid"), element =>
            Attribute(element, "Visibility") == "{Binding IsDashboardPage, Converter={StaticResource BoolToVis}}");

        Assert.Empty(Elements(dashboard, "Expander"));
        Assert.Contains(Elements(dashboard, "Button"), element =>
            Attribute(element, "Command") == "{Binding RunDiagnosticsCommand}");
        Assert.Contains(Elements(dashboard, "ItemsControl"), element =>
            Attribute(element, "ItemsSource") == "{Binding AdditionalDrives}");
        Assert.Contains(Elements(dashboard, "TextBlock"), element =>
            Attribute(element, "Text") == "{Binding DashboardSnapshot.CpuSummary, TargetNullValue='…'}");
        Assert.Contains(Elements(dashboard, "TextBlock"), element =>
            Attribute(element, "Text") == "{Binding DiagnosticSummary}");
        Assert.Contains(Elements(dashboard, "Button"), element =>
            Attribute(element, "CommandParameter") == "System");
    }

    [Fact]
    public void ThemeManager_RebuildsAtmosphereForSelectedTheme()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "dll/WinOptimizer.Settings/ThemeManager.cs"));
        Assert.Contains("#3FB950", source);
        Assert.Contains("#0D1117", source);
        Assert.Contains("#1F6F3D", source);
        Assert.Contains("Terminal Control Desk", source);
    }

    [Fact]
    public void TerminalPalette_IsPresentInDarkTheme()
    {
        var theme = File.ReadAllText(Path.Combine(RepositoryRoot(), "Themes/DarkTheme.xaml"));
        Assert.Contains("#0D1117", theme);
        Assert.Contains("#010409", theme);
        Assert.Contains("#161B22", theme);
        Assert.Contains("#3FB950", theme);
        Assert.Contains("#1F6F3D", theme);
        Assert.Contains("AccentButton", theme);
        Assert.Contains("NavButton", theme);
    }

    [Fact] public void FirstSampleIsBaseline(){Assert.Null(new CpuUsageTracker().Update(100,200,100));}
    [Fact] public void CpuLoadUsesSystemTimeDeltas(){var t=new CpuUsageTracker();t.Update(100,200,100);Assert.Equal(80d,t.Update(120,260,140));}
    [Fact] public void CounterResetIsUnavailable(){var t=new CpuUsageTracker();t.Update(100,200,100);Assert.Null(t.Update(90,220,120));}

    [Fact]
    public void GpuTelemetry_ExposesWindowsDetectedDriverMetadata()
    {
        var service = File.ReadAllText(Path.Combine(
            UiStructureTests.RepositoryRoot(), "Services/GpuTelemetryService.cs"));

        Assert.Contains("Win32_VideoController", service);

        var snapshot = new GpuAdapterActivitySnapshot(0, null, DateTime.UtcNow)
        {
            AdapterName = "Test graphics adapter",
            DriverVersion = "31.0.15.1234",
            DriverStatus = "OK",
        };

        Assert.Equal("Test graphics adapter", snapshot.DisplayName);
        Assert.Equal("Драйвер Windows 31.0.15.1234 · OK", snapshot.DriverInfoText);
    }
}
