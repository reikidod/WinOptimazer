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
        (string?)element.Attribute(name);

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
        var tweakPanel = Assert.Single(expanders.Where(element =>
            Attribute(element, "Header") == "Список сложных твиков"));
        var categoryFilters = Assert.Single(expanders.Where(element =>
            Attribute(element, "Header") == "Фильтры категорий"));

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
    public void DebloatPage_UsesGroupedHeaderActionsAndResponsiveFilterPanel()
    {
        var document = LoadView("Views/MainWindowPages.xaml");

        Assert.Contains(Elements(document, "TextBlock"), element =>
            Attribute(element, "Text") == "Управление установленными программами и приложениями Windows");

        Assert.Contains(Elements(document, "Expander"), element =>
            Attribute(element, "Header") == "Безопасность и возможности удаления" &&
            Attribute(element, "IsExpanded") == "False");

        Assert.Contains(Elements(document, "TextBox"), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "Поиск приложений"));

        Assert.Contains(Elements(document, "RadioButton"), element =>
            Attribute(element, "GroupName") == "DebloatFilter" &&
            Attribute(element, "CommandParameter") == "3");
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
        var aboutTitle = Elements(document, "TextBlock")
            .Single(element => Attribute(element, "Text") == "О программе");

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

}
