using AXamlIDE.CompletionEngine.Parsing;

namespace AXamlIDE.Tests;

/// <summary>The XAML parser the completion engine uses to work out "where is the caret?".</summary>
public class XmlParserTests
{
    [Fact]
    public void Inside_an_attribute_value()
    {
        var parser = XmlParser.Parse("<Grid><Button Content=\"Hel");

        Assert.Equal(XmlParser.ParserState.AttributeValue, parser.State);
        Assert.Equal("Button", parser.TagName);
        Assert.Equal("Content", parser.AttributeName);
        Assert.Equal("Hel", parser.AttributeValue);
    }

    [Fact]
    public void Typing_an_element_name()
    {
        var parser = XmlParser.Parse("<Grid><Butt");

        Assert.Equal(XmlParser.ParserState.StartElement, parser.State);
        Assert.Equal("Butt", parser.TagName);
    }

    [Fact]
    public void Parent_tag_is_known()
    {
        var parser = XmlParser.Parse("<StackPanel><TextBlock ");

        Assert.Equal("TextBlock", parser.TagName);
        Assert.Equal("StackPanel", parser.GetParentTagName(1));
    }
}
