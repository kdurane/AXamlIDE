using System.Xml.Linq;
using AXamlIDE.Services.Preview;

namespace AXamlIDE.Tests;

public sealed class PreviewThemeVariantTests
{
    private const string UserControl = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="App.Views.Main">
          <UserControl.Styles><Style Selector="TextBlock" /></UserControl.Styles>
          <StackPanel><TextBlock Text="Hi" /></StackPanel>
        </UserControl>
        """;

    [Fact]
    public void A_user_control_keeps_its_root_and_wraps_its_content()
    {
        var root = XDocument.Parse(PreviewThemeVariant.Apply(UserControl, "Dark")).Root!;
        XNamespace avalonia = "https://github.com/avaloniaui";

        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.NotNull(root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml")));
        var scope = root.Element(avalonia + "ThemeVariantScope")!;
        Assert.Equal("Dark", scope.Attribute("RequestedThemeVariant")!.Value);
        Assert.NotNull(scope.Element(avalonia + "Border")!.Element(avalonia + "StackPanel"));
        Assert.NotNull(root.Element(avalonia + "UserControl.Styles"));   // property elements stay put
        Assert.Null(root.Element(avalonia + "StackPanel"));
    }

    [Fact]
    public void No_xmlns_declarations_end_up_below_the_root()
    {
        var root = XDocument.Parse(PreviewThemeVariant.Apply(UserControl, "Light")).Root!;
        Assert.All(root.Descendants(), element => Assert.DoesNotContain(element.Attributes(), attribute => attribute.IsNamespaceDeclaration));
    }

    [Fact]
    public void A_window_gets_the_attribute_on_its_root()
    {
        const string window = """<Window xmlns="https://github.com/avaloniaui"><Grid /></Window>""";
        Assert.Equal("Light", XDocument.Parse(PreviewThemeVariant.Apply(window, "Light")).Root!.Attribute("RequestedThemeVariant")!.Value);
    }

    [Fact]
    public void No_theme_and_unwrappable_roots_are_left_alone()
    {
        Assert.Equal(UserControl, PreviewThemeVariant.Apply(UserControl, "No theme"));
        const string styles = """<Styles xmlns="https://github.com/avaloniaui"><Style Selector="Button" /></Styles>""";
        Assert.Equal(styles, PreviewThemeVariant.Apply(styles, "Dark"));
        const string panel = """<StackPanel xmlns="https://github.com/avaloniaui"><TextBlock /><TextBlock /></StackPanel>""";
        Assert.Equal(panel, PreviewThemeVariant.Apply(panel, "Dark"));
    }
}
