using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Tests;

public class ReferencePreviewTests
{
    [Fact]
    public void Indentation_goes_and_the_reference_is_cut_out()
    {
        var preview = ReferencePreview.Of("        (SelectedCategory == AllCategories)  ", 9, 25);

        Assert.Equal(new ReferencePreview("(", "SelectedCategory", " == AllCategories)"), preview);
    }

    [Fact]
    public void A_long_line_keeps_some_context_either_side()
    {
        var line = new string('a', 100) + " Name " + new string('b', 100);

        var preview = ReferencePreview.Of(line, 101, 105);

        Assert.Equal("Name", preview.Match);
        Assert.StartsWith("…", preview.Before);
        Assert.EndsWith("…", preview.After);
        Assert.True(preview.Before.Length <= ReferencePreview.Context + 1);
        Assert.True(preview.After.Length <= ReferencePreview.Context + 1);
    }

    [Fact]
    public void Positions_past_the_line_are_clamped()
    {
        Assert.Equal(new ReferencePreview("abc", "", ""), ReferencePreview.Of("abc", 10, 20));
        Assert.Equal(new ReferencePreview("a", "bc", ""), ReferencePreview.Of("abc", 1, 99));
    }
}
