using AXamlIDE.Services.Indentation;

namespace AXamlIDE.Tests;

/// <summary>Where Enter puts a new line of XAML. "|" marks where the new line starts.</summary>
public class XamlIndentationTests
{
    private static string Indent(string textWithBar)
    {
        var lineStart = textWithBar.IndexOf('|');
        return XamlIndentation.IndentFor(textWithBar.Remove(lineStart, 1), lineStart, "    ");
    }

    [Fact]
    public void Inside_an_element_goes_one_level_in()
        => Assert.Equal("        ", Indent("<Window>\n    <Grid>\n|"));

    [Fact]
    public void After_a_closed_child_stays_level_with_it()
        => Assert.Equal("        ", Indent("<Window>\n    <Grid>\n        <Button/>\n|"));

    [Fact]
    public void After_a_closing_tag_goes_back_out()
        => Assert.Equal("    ", Indent("<Window>\n    <Grid>\n    </Grid>\n|"));

    [Fact]
    public void A_closing_tag_lines_up_with_its_opening_tag()
        => Assert.Equal("    ", Indent("<Window>\n    <Grid>\n        <Button/>\n|</Grid>"));

    [Fact]
    public void Attribute_lines_up_under_the_first_attribute()
        => Assert.Equal("            ", Indent("<Window>\n    <Button Width=\"10\"\n|"));

    [Fact]
    public void Attribute_after_a_bare_tag_name_goes_one_level_in()
        => Assert.Equal("        ", Indent("<Window>\n    <StackPanel\n|"));

    [Fact]
    public void Root_level_has_no_indent()
        => Assert.Equal("", Indent("<?xml version=\"1.0\"?>\n|"));

    [Fact]
    public void Enter_between_an_empty_tag_pair()
    {
        const string text = "<Window>\n    <Grid>\n</Grid>";
        Assert.True(XamlIndentation.IsBetweenEmptyTagPair(text, text.IndexOf("</Grid>")));
        Assert.False(XamlIndentation.IsBetweenEmptyTagPair("<Grid><Button/>\n</Grid>", 16));
    }

    [Theory]
    [InlineData("<Grid>\n\t<Button/>\n</Grid>", true)]
    [InlineData("<Grid>\n    <Button/>\n</Grid>", false)]
    [InlineData("<Grid/>", false)]
    public void Detects_tabs_or_spaces(string text, bool tabs)
        => Assert.Equal(tabs, IndentDetection.UsesTabs(text));
}
