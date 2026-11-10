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
    public void AdvancedTweaks_KeepFiltersAndListIndependentlyCollapsible()
    {
        var document = LoadView("Views/MainWindow.xaml");
        var expanders = Elements(document, "Expander").ToList();

        Assert.Contains(expanders, element =>
            Attribute(element, "Header") == "Фильтры сложных твиков" &&
            Attribute(element, "IsExpanded") == "False");

        Assert.Contains(expanders, element =>
            Attribute(element, "Header") == "Список сложных твиков" &&
            Attribute(element, "IsExpanded") == "True");
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
    public void AboutSection_RemainsVisibleOutsideCollapsibleSections()
    {
        var document = LoadView("dll/WinOptimizer.Settings/Views/SettingsPage.xaml");
        var aboutTitle = Elements(document, "TextBlock")
            .Single(element => Attribute(element, "Text") == "О программе");

        Assert.DoesNotContain(aboutTitle.Ancestors(), ancestor => ancestor.Name.LocalName == "Expander");
        Assert.Contains(Elements(document, "ContentPresenter"), element =>
            Attribute(element, "ContentSource") == "Content");
    }
}
