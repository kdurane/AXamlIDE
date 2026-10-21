using System.Linq;
using AXamlIDE.CompletionEngine.Parsing;

namespace AXamlIDE.Tests;

/// <summary>The character scanners the completion engine uses for start tags and selectors.</summary>
public class XamlScannerTests
{
    [Fact]
    public void Start_tag_attributes_with_either_quote()
    {
        var tag = XamlTagScanner.ReadStartTag("<Button Content='Hi' x:Name=\"Go\" Width=\"1", 0);

        Assert.NotNull(tag);
        Assert.Equal("Button", tag.Name);
        Assert.Equal(["Content", "x:Name", "Width"], tag.Attributes.Select(a => a.Name));
        Assert.Equal("Hi", tag.Attributes[0].Value);
        Assert.Equal("Go", tag.Attributes[1].Value);
        Assert.Null(tag.Attributes[2].Value); // not finished yet
    }

    [Fact]
    public void Named_elements_and_resource_keys_skip_comments()
    {
        const string xaml = "<StackPanel><!-- <Button Name=\"Old\"/> --><TextBox x:Name=\"Search\"/>"
                            + "<SolidColorBrush x:Key=\"Accent\"/></StackPanel>";

        Assert.Equal([("Search", "TextBox")], XamlTagScanner.NamedElements(xaml));
        Assert.Equal(["Accent"], XamlTagScanner.ResourceKeys(xaml));
    }

    [Theory]
    [InlineData("Button.primary:pointerover", "Button")]
    [InlineData("Button:pointerover /template/ ContentPresenter#PART_ContentPresenter", "ContentPresenter")]
    [InlineData("local|MyControl[IsEnabled=True]", "local|MyControl")]
    [InlineData(".primary", null)]
    [InlineData("^:pressed", null)]
    public void Last_type_in_a_selector(string selector, string? expected)
        => Assert.Equal(expected, SelectorScanner.LastTypeName(selector));

    [Fact]
    public void Style_classes_in_a_selector()
        => Assert.Equal(["primary", "big"], SelectorScanner.StyleClasses("Button.primary.big[(Grid.Row)=1]:pointerover"));

    [Theory]
    [InlineData("Button.pri", true, 7)]
    [InlineData("Button.", true, 7)]
    [InlineData("Button[(Grid.Ro", false, 0)]
    [InlineData("Button:point", false, 0)]
    public void Ends_in_a_style_class(string selector, bool expected, int expectedStart)
    {
        Assert.Equal(expected, SelectorScanner.EndsInStyleClass(selector, out var start));
        if (expected)
            Assert.Equal(expectedStart, start);
    }

    [Fact]
    public void Markup_extension_quoted_value_keeps_its_commas()
    {
        var parser = MarkupExtensionParser.Parse("{Binding StringFormat='{}{0:N2}, more', Mo");

        Assert.Equal(MarkupExtensionParser.ParserStateType.StartAttribute, parser.State);
        Assert.Equal("Binding", parser.ElementName?.Trim());
    }
}
