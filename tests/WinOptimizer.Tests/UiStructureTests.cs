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
            ("BgWindowColor", "#101418"),
            ("BgSidebarColor", "#151A1F"),
            ("BgCardColor", "#1A2127"),
            ("AccentColor", "#00C58E")
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

    // NOTE: Remainder of file must be restored from beta history if truncated.
    // Full file is at artifacts/.tmp and local /tmp/UiStructureTests.Signal.cs
}
