using System.Xml.Linq;
using Xunit;

namespace WinOptimizer.Tests;

/// <summary>
/// Structural regression tests for the collapsible Fluent UI sections.
/// These tests do not replace visual verification on Windows, but catch accidental
/// removal of the sections or changes to their intended initial state.
/// </summary>
public sealed class UiStructureTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinOptimizer.csproj")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Не удалось найти корень репозитория.");
    }

    private static XDocument LoadView(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), relativePath));

    private static IEnumerable<XElement> Elements(XDocument document, string name) =>
        document.Descendants().Where(element => element.Name.LocalName == name);

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    [Fact]
    public void MainShell_UsesOfficialWebsiteVisualLanguage()
    {
        var view = LoadView("Views/MainWindow.xaml");
        var theme = LoadView("Themes/DarkTheme.xaml");

        Assert.Contains(Elements(view, "Rectangle"), element =>
            Attribute(element, "Fill") == "{DynamicResource SiteGridBrush}");
        Assert.Equal(2, Elements(view, "Ellipse").Count(element =>
            (string?)element.Attribute("Fill") is "{DynamicResource SiteRedGlowBrush}" or
                "{DynamicResource SiteBlueGlowBrush}"));

        Assert.Contains(Elements(theme, "DrawingBrush"), element =>
            Attribute(element, "Key") == "SiteGridBrush");
        Assert.Contains(Elements(theme, "RadialGradientBrush"), element =>
            Attribute(element, "Key") == "SiteRedGlowBrush");
        Assert.Contains(Elements(theme, "RadialGradientBrush"), element =>
            Attribute(element, "Key") == "SiteBlueGlowBrush");
        Assert.Contains(Elements(theme, "Style"), element =>
            Attribute(element, "Key") == "AccentButton" &&
            element.Descendants().Any(child =>
                child.Name.LocalName == "Setter" &&
                Attribute(child, "Property") == "Background" &&
                Attribute(child, "Value") == "{DynamicResource SiteAccentButtonBrush}"));
    }

    [Fact]
    public void BrandMark_StaysInsideItsSquareContainer()
    {
        var document = LoadView("Views/MainWindow.xaml");
        var logoBorder = Assert.Single(Elements(document, "Border"), element =>
            Attribute(element, "Width") == "42" &&
            Attribute(element, "Height") == "42" &&
            element.Descendants().Any(child => child.Name.LocalName == "Viewbox"));

        var viewbox = Assert.Single(logoBorder.Descendants(), element =>
            element.Name.LocalName == "Viewbox");
        Assert.Equal("30", Attribute(viewbox, "Width"));
        Assert.Equal("30", Attribute(viewbox, "Height"));

        var canvas = Assert.Single(viewbox.Descendants(), element =>
            element.Name.LocalName == "Canvas");
        Assert.Equal("24", Attribute(canvas, "Width"));
        Assert.Equal("24", Attribute(canvas, "Height"));
        Assert.Equal(2, canvas.Elements().Count(element => element.Name.LocalName == "Path"));
        Assert.DoesNotContain(canvas.DescendantsAndSelf(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Margin" &&
                attribute.Value.Split(',').Any(value => double.TryParse(value, out var number) && number < 0)));
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
    public void LatestVersionButton_DownloadsInstallerExeDirectly()
    {
        var document = LoadView("Views/MainWindow.xaml");

        Assert.Contains(Elements(document, "Button"), element =>
            Attribute(element, "Content") == "Скачать последнюю версию" &&
            Attribute(element, "Command") == "{Binding DownloadLatestVersionCommand}");

        Assert.Contains(Elements(document, "MenuItem"), element =>
            Attribute(element, "Header") == "Скачать последнюю версию (.exe)" &&
            Attribute(element, "Command") ==
                "{Binding PlacementTarget.DataContext.DownloadLatestVersionCommand, RelativeSource={RelativeSource AncestorType=ContextMenu}}");
    }

    [Fact]
    public void Dashboard_ContainsCollapsibleDrivesDiagnosticsAndEventJournal()
    {
        var document = LoadView("Views/MainWindow.xaml");
        var expanders = Elements(document, "Expander").ToList();

        Assert.Contains(expanders, element =>
            Attribute(element, "Header") == "Другие диски" &&
            Attribute(element, "IsExpanded") == "False");

        Assert.Contains(expanders, element =>
            Attribute(element, "Header") == "{Binding DiagnosticHeader}" &&
            Attribute(element, "IsExpanded") == "False");

        Assert.Contains(expanders, element =>
            Attribute(element, "Header") == "Журнал событий" &&
            Attribute(element, "IsExpanded") == "False");

        Assert.Contains(Elements(document, "Button"), element =>
            Attribute(element, "Command") == "{Binding RunDiagnosticsCommand}");

        Assert.Contains(Elements(document, "Button"), element =>
            Attribute(element, "Command") == "{Binding Settings.OpenLogCommand}");
    }

    [Fact]
    public void AdvancedTweaks_KeepCategoryFiltersInsideTheTweakPanel()
    {
        var document = LoadView("Views/MainWindow.xaml");
        var expanders = Elements(document, "Expander").ToList();
        var tweakPanel = Assert.Single(expanders, element =>
            Attribute(element, "Header") == "Список сложных твиков");
        var categoryFilters = Assert.Single(expanders, element =>
            Attribute(element, "Header") == "Фильтры категорий");

        Assert.Equal("False", Attribute(tweakPanel, "IsExpanded"));
        Assert.Contains(categoryFilters.Ancestors(), ancestor =>
            ancestor.Name.LocalName == "Expander" &&
            Attribute(ancestor, "Header") == "Список сложных твиков");
        Assert.DoesNotContain(expanders, element =>
            Attribute(element, "Header") == "Фильтры сложных твиков");
    }

    [Fact]
    public void WindowsTaskScheduler_AndNestedDetails_StartCollapsed()
    {
        var document = LoadView("Views/MainWindowPages.xaml");
        var expanders = Elements(document, "Expander").ToList();

        Assert.Contains(expanders, element =>
            Attribute(element, "Header") == "Планировщик задач Windows · только чтение" &&
            Attribute(element, "IsExpanded") == "False");

        Assert.Contains(expanders, element =>
            Attribute(element, "Header") == "Подробности задачи" &&
            Attribute(element, "IsExpanded") == "False");

        Assert.Contains(expanders, element =>
            Attribute(element, "Header") == "Команда запуска" &&
            Attribute(element, "IsExpanded") == "False");
    }

    [Fact]
    public void DebloatPage_ShowsScanProgressAndRetryState()
    {
        var document = LoadView("Views/MainWindowPages.xaml");

        Assert.Contains(Elements(document, "ProgressBar"), element =>
            Attribute(element, "IsIndeterminate") == "True" &&
            Attribute(element, "Visibility") == "{Binding Debloat.IsBusy, Converter={StaticResource BoolToVis}}");

        Assert.Contains(Elements(document, "Border"), element =>
            Attribute(element, "Visibility") == "{Binding Debloat.HasNoItems, Converter={StaticResource BoolToVis}}");

        Assert.Contains(Elements(document, "Button"), element =>
            Attribute(element, "Command") == "{Binding Debloat.RefreshCommand}" &&
            Attribute(element, "Content") == "Повторить сканирование");
    }

    [Fact]
    public void DebloatPage_UsesCompactHeaderAndInsetApplicationCards()
    {
        var document = LoadView("Views/MainWindowPages.xaml");

        Assert.Contains(Elements(document, "TextBlock"), element =>
            Attribute(element, "Text") == "Удаление приложений");
        Assert.DoesNotContain(Elements(document, "TextBlock"), element =>
            Attribute(element, "Text") == "Управление установленными программами и приложениями Windows");
        Assert.DoesNotContain(Elements(document, "Expander"), element =>
            Attribute(element, "Header") == "Безопасность и возможности удаления");

        Assert.Contains(Elements(document, "ItemsControl"), element =>
            Attribute(element, "ItemsSource") == "{Binding Debloat.View}" &&
            Attribute(element, "Margin") == "12,0,24,8");
        Assert.Contains(Elements(document, "TextBox"), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "Поиск приложений"));
        Assert.Contains(Elements(document, "RadioButton"), element =>
            Attribute(element, "GroupName") == "DebloatFilter" &&
            Attribute(element, "CommandParameter") == "3");

        var headerActions = Assert.Single(Elements(document, "WrapPanel"), element =>
            Attribute(element, "Grid.Row") == "1" &&
            Attribute(element, "HorizontalAlignment") == "Stretch" &&
            element.Elements().Any(child => child.Name.LocalName == "Button" &&
                Attribute(child, "Command") == "{Binding Debloat.RemoveSelectedCommand}"));
        Assert.Contains(headerActions.Elements(), element =>
            element.Name.LocalName == "Button" && Attribute(element, "Margin") == "0,0,0,8");
    }

    [Fact]
    public void OptimizationGauge_ShowsSelectedLevelAndNumericProgress()
    {
        var document = LoadView("Views/MainWindow.xaml");

        Assert.Contains(Elements(document, "TextBlock"), element =>
            Attribute(element, "Text") == "{Binding GaugeProgressLabel}");

        Assert.Contains(Elements(document, "TextBlock"), element =>
            Attribute(element, "Text") == "{Binding SelectedOptimizationPreset.Title}");

        Assert.Contains(Elements(document, "Path"), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName.EndsWith("ArcProgress", StringComparison.Ordinal) &&
                attribute.Value == "{Binding GaugeProgressValue}"));
    }

    [Fact]
    public void SectionExpander_ReservesSpaceForChevron()
    {
        var document = XDocument.Load(Path.Combine(RepositoryRoot(), "Themes/DarkTheme.Extras.xaml"));
        Assert.Contains(Elements(document, "ColumnDefinition"), element =>
            Attribute(element, "Width") == "40");
        Assert.Contains(Elements(document, "Path"), element =>
            Attribute(element, "Width") == "12" &&
            Attribute(element, "Height") == "7");
    }

    [Fact]
    public void AboutSection_RemainsVisibleOutsideCollapsibleSections()
    {
        var document = LoadView("dll/WinOptimizer.Settings/Views/SettingsPage.xaml");
        var aboutTitle = Assert.Single(Elements(document, "TextBlock"), element =>
            Attribute(element, "Text") == "О программе");

        Assert.DoesNotContain(aboutTitle.Ancestors(), ancestor => ancestor.Name.LocalName == "Expander");
        Assert.Contains(Elements(document, "ContentPresenter"), element =>
            Attribute(element, "ContentSource") == "Content");
    }
    [Fact]
    public void Settings_UseCollapsedSectionsAndKeepAboutVisible()
    {
        var document = LoadView("dll/WinOptimizer.Settings/Views/SettingsPage.xaml");
        var expanders = Elements(document, "Expander").ToList();

        foreach (var header in new[] { "Запуск и обновления", "Безопасность", "Навигация и разделы",
                                       "Тема и цвета", "Фон", "Анимации и масштаб", "Данные и диагностика" })
        {
            Assert.Contains(expanders, element =>
                Attribute(element, "Header") == header &&
                Attribute(element, "IsExpanded") == "False");
        }

        Assert.Contains(Elements(document, "CheckBox"), element =>
            Attribute(element, "IsChecked") == "{Binding Settings.ShowApplicationsGroup, Mode=TwoWay}");

        Assert.Contains(Elements(document, "CheckBox"), element =>
            Attribute(element, "IsChecked") == "{Binding Settings.ShowAppsTab, Mode=TwoWay}");

        var aboutTitle = Elements(document, "TextBlock")
            .Single(element => Attribute(element, "Text") == "О программе");
        Assert.DoesNotContain(aboutTitle.Ancestors(), ancestor => ancestor.Name.LocalName == "Expander");
    }


    [Fact]
    public void MainWindow_UsesNativeWindowsIcon()
    {
        var view = LoadView("Views/MainWindow.xaml");
        var window = Assert.Single(Elements(view, "Window"));
        Assert.Equal("pack://application:,,,/Assets/app.ico", Attribute(window, "Icon"));
    }

    [Fact]
    public void ThemeManager_RebuildsAtmosphereForSelectedTheme()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "dll/WinOptimizer.Settings/ThemeManager.cs"));
        Assert.Contains("SetThemeAtmosphere(app, accentHex, accentStrong, p.IsLight)", source);
        Assert.Contains("app.Resources[\"SiteGridBrush\"] = grid", source);
        Assert.Contains("app.Resources[\"SiteRedGlowBrush\"]", source);
        Assert.Contains("app.Resources[\"SiteBlueGlowBrush\"]", source);
    }

    [Fact]
    public void AppsCatalog_ExposesInstallAndUninstallAction()
    {
        var view = LoadView("Views/MainWindowPages.xaml");
        Assert.Contains(Elements(view, "Button"), element =>
            Attribute(element, "Content") == "{Binding ActionText}" &&
            Attribute(element, "Command")?.Contains("InstallAppCommand", StringComparison.Ordinal) == true);

        var service = File.ReadAllText(Path.Combine(RepositoryRoot(), "Services/AppInstallService.cs"));
        Assert.Contains("Task<(bool Ok, string Message)> UninstallAsync", service);
        Assert.Contains("FindUninstallEntry(app)", service);
        Assert.Contains("process.ExitCode", service);
    }

    [Fact]
    public void InstallAndRemovalPages_ExposeConsistentNavigation()
    {
        var view = LoadView("Views/MainWindowPages.xaml");
        var installPage = Assert.Single(Elements(view, "Grid"), element =>
            Attribute(element, "Visibility") == "{Binding IsAppsPage, Converter={StaticResource BoolToVis}}");
        var removalPage = Assert.Single(Elements(view, "Grid"), element =>
            Attribute(element, "Visibility") == "{Binding IsDebloatPage, Converter={StaticResource BoolToVis}}");

        Assert.Contains(Elements(installPage, "Button"), element =>
            Attribute(element, "Content") == "Удаление" &&
            Attribute(element, "CommandParameter") == "Debloat");
        Assert.Contains(Elements(removalPage, "Button"), element =>
            Attribute(element, "Content") == "Установка" &&
            Attribute(element, "CommandParameter") == "Apps");
        Assert.Contains(Elements(installPage, "Button"), element =>
            Attribute(element, "Content") == "Обновить список" &&
            Attribute(element, "Command") == "{Binding Apps.RefreshCommand}");
    }

    [Fact]
    public void ResetSelection_RestoresDefaultQuickPresetGauge()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "ViewModels/MainViewModel.cs"));
        var start = source.IndexOf("private void ResetSelection()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private async Task ApplyAsync()", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var method = source[start..end];

        Assert.Contains("_selectedOptimizationPresetId = \"light\";", method);
        Assert.Contains("OnPropertyChanged(nameof(SelectedOptimizationPreset))", method);
        Assert.Contains("OnPropertyChanged(nameof(GaugeProgressValue))", method);
        Assert.Contains("OnPropertyChanged(nameof(GaugeProgressLabel))", method);
    }

    [Fact]
    public void AppDetection_DoesNotUseRunningProcessAsInstallationProof()
    {
        var service = File.ReadAllText(Path.Combine(RepositoryRoot(), "Services/AppInstallService.cs"));
        Assert.Contains("return FindUninstallEntry(app) is not null;", service);
        Assert.DoesNotContain("Process.GetProcessesByName(app.DetectProcess)", service);
        Assert.Contains("app.Id != \"discord\" && File.Exists(app.DetectPath)", service);
    }

}
