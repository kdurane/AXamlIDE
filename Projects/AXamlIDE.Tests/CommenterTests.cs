using System.Collections.Generic;
using AXamlIDE.Services.Commenting;
using AXamlIDE.Services.CSharp;

namespace AXamlIDE.Tests;

public sealed class CommenterTests
{
    /// <summary>
    /// Run a commenter on <paramref name="before"/>, where '|' marks the caret, or "[[" and "]]" the selection,
    /// and return the text with the edits applied.
    /// </summary>
    private static string Run(string before, System.Func<string, int, int, IReadOnlyList<TextEdit>> commenter)
    {
        int start, end;
        string text;
        if (before.Contains("[["))
        {
            start = before.IndexOf("[[");
            text = before.Remove(start, 2);
            end = text.IndexOf("]]");
            text = text.Remove(end, 2);
        }
        else
        {
            start = end = before.IndexOf('|');
            text = before.Remove(start, 1);
        }

        foreach (var edit in commenter(text, start, end))   // listed from the end of the text
            text = text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.Replacement);
        return text;
    }

    private static string CSharp(string before, CommentMode mode = CommentMode.Toggle)
        => Run(before, (text, start, end) => LineCommenter.Apply(text, start, end, mode));

    private static string Xml(string before, CommentMode mode = CommentMode.Toggle)
        => Run(before, (text, start, end) => XmlCommenter.Apply(text, start, end, mode));

    // ------------------------------------------------------------------ C#

    [Fact]
    public void CSharp_caret_comments_its_line()
        => Assert.Equal("a();\n    // b();\nc();", CSharp("a();\n    b|();\nc();"));

    [Fact]
    public void CSharp_markers_line_up_at_the_smallest_indent_and_skip_blank_lines()
        => Assert.Equal(
            "    // if (x)\n\n    //     y();\n",
            CSharp("    [[if (x)\n\n        y();]]\n"));

    [Fact]
    public void CSharp_toggle_uncomments_when_every_line_is_commented()
        => Assert.Equal("    if (x)\n        y();\n", CSharp("    [[// if (x)\n    //     y();]]\n"));

    [Fact]
    public void CSharp_mixed_lines_get_commented()
        => Assert.Equal("// // a\n// b\n", CSharp("[[// a\nb]]\n"));

    [Fact]
    public void CSharp_whole_line_selection_leaves_the_next_line_alone()
        => Assert.Equal("// a\r\n// b\r\nc\r\n", CSharp("[[a\r\nb\r\n]]c\r\n"));

    [Fact]
    public void CSharp_uncomment_skips_lines_that_are_not_comments()
        => Assert.Equal("a\nb\n", CSharp("[[//a\nb]]\n", CommentMode.Uncomment));

    // ------------------------------------------------------------------ AXAML

    private const string Panel =
        """
        <StackPanel
            Orientation="Horizontal"
            Spacing="2">
            <TextBlock
                Margin="8,0"
                Text="{Binding Summary}" />
            <Button Classes="icon">
                <PathIcon Data="{DynamicResource Icon.ChevronDown}" />
            </Button>
        </StackPanel>
        """;

    private static string At(string marker) => Panel.Replace(marker, "|" + marker);

    [Fact]
    public void Xml_caret_on_the_start_tag_comments_the_whole_element()
        => Assert.Equal("<!--" + Panel + "-->", Xml(At("<StackPanel")));

    [Fact]
    public void Xml_caret_on_an_attribute_or_end_tag_comments_the_whole_element()
    {
        Assert.Equal("<!--" + Panel + "-->", Xml(At("Spacing")));
        Assert.Equal("<!--" + Panel + "-->", Xml(At("</StackPanel>")));
    }

    [Fact]
    public void Xml_caret_in_a_child_comments_only_that_child()
    {
        var expected = Panel.Replace("<TextBlock", "<!--<TextBlock").Replace("Summary}\" />", "Summary}\" />-->");
        Assert.Equal(expected, Xml(At("Margin")));
    }

    [Fact]
    public void Xml_child_with_children_is_commented_whole()
    {
        var expected = Panel.Replace("<Button", "<!--<Button").Replace("</Button>", "</Button>-->");
        Assert.Equal(expected, Xml(At("</Button>")));
    }

    [Fact]
    public void Xml_selection_over_two_children_grows_to_both()
    {
        var before = Panel.Replace("Text=", "[[Text=").Replace("Classes=", "Classes]]=");
        var expected = Panel.Replace("<TextBlock", "<!--<TextBlock").Replace("</Button>", "</Button>-->");
        Assert.Equal(expected, Xml(before));
    }

    [Fact]
    public void Xml_caret_anywhere_in_a_comment_uncomments_all_of_it()
    {
        var commented = Panel.Replace("<TextBlock", "<!--<TextBlock").Replace("Summary}\" />", "Summary}\" />-->");
        Assert.Equal(Panel, Xml(commented.Replace("Margin", "|Margin")));
    }

    [Fact]
    public void Xml_round_trip()
    {
        var commented = Xml(At("<StackPanel"));
        Assert.Equal(Panel, Xml(commented.Replace("Spacing", "|Spacing")));
    }

    [Fact]
    public void Xml_spaces_inside_the_markers_go_on_uncomment()
        => Assert.Equal("<A>\n    <B />\n</A>", Xml("<A>\n    <!-- <B />| -->\n</A>"));

    [Fact]
    public void Xml_comments_inside_the_range_are_kept_and_the_rest_is_commented_around_them()
        => Assert.Equal(
            "<!--<A>\n    <B />-->\n    <!-- note -->\n    <!--<C />\n</A>-->",
            Xml("<A|>\n    <B />\n    <!-- note -->\n    <C />\n</A>"));

    [Fact]
    public void Xml_comment_mode_inside_a_comment_does_nothing()
        => Assert.Equal("<!-- <B /> -->", Xml("<!-- <B |/> -->", CommentMode.Comment));

    [Fact]
    public void Xml_blank_line_does_nothing()
        => Assert.Equal("<A>\n    |\n</A>".Replace("|", ""), Xml("<A>\n    |\n</A>"));

    [Fact]
    public void Xml_angle_brackets_in_attribute_values_are_not_tags()
        => Assert.Equal("<A>\n    <!--<B Text=\"a < b > c\" />-->\n</A>", Xml("<A>\n    <B Text=\"a < b > c\" |/>\n</A>"));

    [Fact]
    public void Xml_half_typed_element_comments_just_its_start_tag()
        => Assert.Equal("<A>\n    <!--<B Text=\"x\">-->\n</A>", Xml("<A>\n    <B Text=\"x\"|>\n</A>"));
}
