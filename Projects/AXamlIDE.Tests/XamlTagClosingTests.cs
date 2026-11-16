using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Tests;

public sealed class XamlTagClosingTests
{
    /// <summary>Whether the tag just before '|' (its '>' just typed) is already closed.</summary>
    private static bool Closed(string textWithCaret, string tagName)
    {
        var caret = textWithCaret.IndexOf('|');
        return XamlTagClosing.HasEndTag(textWithCaret.Remove(caret, 1), caret, tagName);
    }

    [Fact]
    public void A_retyped_gt_finds_the_end_tag_past_the_content()
        => Assert.True(Closed("""
            <StackPanel>
                <Grid>|
                    <Button Content="a > b" />
                    <Grid><TextBlock /></Grid>
                    <!-- </StackPanel> -->
                </Grid>
            </StackPanel>
            """, "Grid"));

    [Fact]
    public void A_new_element_is_not_closed_by_its_parent()
        => Assert.False(Closed("""
            <StackPanel>
                <Grid>|
                <Button />
            </StackPanel>
            """, "Grid"));

    [Fact]
    public void A_new_Grid_in_a_Grid_is_not_closed_by_the_outer_end_tag()
        => Assert.False(Closed("""
            <Grid>
                <Grid>|
            </Grid>
            """, "Grid"));

    [Fact]
    public void An_end_tag_on_the_same_line_counts()
        => Assert.True(Closed("<Grid><Grid>|text</Grid></Grid>", "Grid"));

    [Fact]
    public void At_the_end_of_the_text_it_is_not_closed()
        => Assert.False(Closed("<StackPanel>\n    <Grid>|", "Grid"));

    [Theory]
    [InlineData("<Grid/></Grid>", 6, "Grid", 7, 7)]
    [InlineData("<Grid/>\n    </Grid >", 6, "Grid", 7, 13)]
    public void An_empty_elements_end_tag_is_found(string text, int gt, string tag, int start, int length)
        => Assert.Equal((start, length), XamlTagClosing.EmptyEndTagAfter(text, gt, tag));

    [Theory]
    [InlineData("<Grid/><Button /></Grid>")]
    [InlineData("<Grid/>text</Grid>")]
    [InlineData("<Grid/></Border>")]
    public void A_non_empty_element_keeps_its_end_tag(string text)
        => Assert.Null(XamlTagClosing.EmptyEndTagAfter(text, 6, "Grid"));
}
