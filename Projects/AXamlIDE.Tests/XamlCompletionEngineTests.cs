using System.Collections.Generic;
using System.Linq;
using AXamlIDE.CompletionEngine;
using AXamlIDE.CompletionEngine.AssemblyMetadata;
using AXamlIDE.CompletionEngine.Completion;
using Engine = AXamlIDE.CompletionEngine.Completion.CompletionEngine;

namespace AXamlIDE.Tests;

/// <summary>
/// Completion metadata read once from the Avalonia assemblies in the test's bin/ (a few seconds), shared by the tests.
/// </summary>
public sealed class AvaloniaMetadataFixture
{
    public Metadata Metadata { get; }

    public AvaloniaMetadataFixture()
    {
        var testAssembly = typeof(AvaloniaMetadataFixture).Assembly.Location;
        var assemblies = new DepsJsonFileAssemblyProvider(testAssembly, testAssembly).GetAssemblies().Distinct().ToArray();
        Metadata = new MetadataReader(new DnlibMetadataProvider()).GetForTargetAssembly(new Assemblies(assemblies))!;
    }

    private sealed class Assemblies(string[] paths) : IAssemblyProvider
    {
        public IEnumerable<string> GetAssemblies() => paths;
    }
}

/// <summary>The XAML completion engine against real Avalonia metadata: it offers only what's valid where the caret is.</summary>
public class XamlCompletionEngineTests(AvaloniaMetadataFixture fixture) : IClassFixture<AvaloniaMetadataFixture>
{
    private const string Root = "<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";

    private (List<string> Items, string Replaces) Complete(string body, XamlProjectInfo? project = null)
    {
        var text = Root + body;
        var set = new Engine().GetCompletions(fixture.Metadata, text, text.Length, "AXamlIDE.Tests", project);
        return (set?.Completions.Select(c => c.DisplayText).ToList() ?? [], set is null ? "" : text[set.StartPosition..]);
    }

    [Fact]
    public void Classes_is_offered_as_an_attribute()
        => Assert.Contains("Classes", Complete("<Button Cl").Items);

    [Fact]
    public void Attributes_already_set_are_not_offered_again()
    {
        var items = Complete("<Button Width=\"10\" Wi").Items;

        Assert.DoesNotContain("Width", items);
    }

    [Fact]
    public void Enum_like_class_values_are_offered()
    {
        var items = Complete("<TextBlock TextTrimming=\"").Items;

        Assert.Contains("CharacterEllipsis", items);
        Assert.Contains("None", items);
    }

    [Fact]
    public void Attribute_after_a_single_quoted_value()
        => Assert.Contains("Width", Complete("<Button Content='Hi' Wi").Items);

    [Fact]
    public void Panel_children_are_controls()
    {
        var items = Complete("<Grid><").Items;

        Assert.Contains("Button", items);
        Assert.DoesNotContain("SolidColorBrush", items);
        Assert.DoesNotContain("RowDefinition", items);
    }

    [Fact]
    public void Collection_property_children_are_its_items()
        => Assert.Equal(["RowDefinition"], Complete("<Grid><Grid.RowDefinitions><").Items.Where(i => !i.StartsWith('/') && i != "!--"));

    [Fact]
    public void Property_element_children_fit_the_property_type()
    {
        var items = Complete("<Button><Button.Background><").Items;

        Assert.Contains("SolidColorBrush", items);
        Assert.DoesNotContain("Button", items);
    }

    [Fact]
    public void Resources_take_anything()
        => Assert.Contains("SolidColorBrush", Complete("<UserControl.Resources><").Items);

    [Fact]
    public void Setter_property_offers_only_avalonia_properties_of_the_selector_type()
    {
        var items = Complete("<UserControl.Styles><Style Selector=\"Button.primary:pointerover\"><Setter Property=\"C").Items;

        Assert.Contains("CornerRadius", items);
        Assert.DoesNotContain("Classes", items); // a CLR property: a Setter can't set it
    }

    [Fact]
    public void Nested_style_setters_use_the_parent_style_type()
        => Assert.Contains("FontSize", Complete("<UserControl.Styles><Style Selector=\"TextBlock\"><Style Selector=\"^:pointerover\"><Setter Property=\"Fo").Items);

    [Fact]
    public void Selector_attached_property_replaces_only_the_typed_name()
    {
        var (items, replaces) = Complete("<UserControl.Styles><Style Selector=\"Button[(Grid.Ro");

        Assert.Contains("Row", items);
        Assert.Equal("Ro", replaces);
    }

    [Fact]
    public void Selector_property_replaces_only_the_typed_name()
    {
        var (items, replaces) = Complete("<UserControl.Styles><Style Selector=\"Button[IsDef");

        Assert.Equal(["IsDefault"], items);
        Assert.Equal("IsDef", replaces);
    }

    [Fact]
    public void Resource_keys_come_from_the_document_and_project()
    {
        var items = Complete(
            "<UserControl.Resources><SolidColorBrush x:Key=\"LocalBrush\"/></UserControl.Resources><Button Background=\"{StaticResource ",
            new XamlProjectInfo(["AppBrush"], [])).Items;

        Assert.Contains("LocalBrush", items);
        Assert.Contains("AppBrush", items);
        Assert.DoesNotContain("ThemeAccentBrush", items); // the old 0.10 theme's keys
    }

    [Fact]
    public void Classes_value_offers_the_project_style_classes()
    {
        var (items, replaces) = Complete("<Button Classes=\"primary to", new XamlProjectInfo([], ["toolbar", "primary"]));

        Assert.Equal(["toolbar"], items);
        Assert.Equal("to", replaces);
    }

    [Fact]
    public void Template_binding_offers_the_target_type_properties()
    {
        var items = Complete("<UserControl.Styles><ControlTheme TargetType=\"Button\"><Setter Property=\"Template\"><ControlTemplate>"
                             + "<Border Background=\"{TemplateBinding ").Items;

        Assert.Contains("Background", items);
        Assert.Contains("CornerRadius", items);
        Assert.DoesNotContain("Orientation", items); // StackPanel's, not Button's
    }

    [Fact]
    public void Binding_to_a_named_element_uses_its_type()
        => Assert.Contains("Text", Complete("<TextBox Name=\"Search\"/><TextBlock Text=\"{Binding #Search.Te").Items);

    [Fact]
    public void Class_directive_only_on_the_root()
        => Assert.DoesNotContain("x:Class", Complete("<Button x:C").Items);
}
