using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Tests;

public sealed class BindingPathLocatorTests
{
    private static BindingPathAtCaret? At(string textWithCaret)
    {
        var offset = textWithCaret.IndexOf('|');
        return BindingPathLocator.Find(textWithCaret.Remove(offset, 1), offset);
    }

    [Theory]
    [InlineData("""ItemsSource="{Binding Run.Startup|Projects}" """, "Run.StartupProjects", 1)]
    [InlineData("""ItemsSource="{Binding R|un.StartupProjects}" """, "Run.StartupProjects", 0)]
    [InlineData("""Text="{Binding |Name, Mode=TwoWay}" """, "Name", 0)]
    [InlineData("""Text="{CompiledBinding Path=Run.Sta|tus}" """, "Run.Status", 1)]
    [InlineData("""Text="{Binding Mode=OneWay, Path=Run.Sta|tus}" """, "Run.Status", 1)]
    [InlineData("""Text="{x:Binding Na|me}" """, "Name", 0)]
    public void Finds_the_path_and_the_segment_under_the_caret(string text, string path, int segment)
    {
        var found = At(text);

        Assert.NotNull(found);
        Assert.Equal(path, found.Path);
        Assert.Equal(segment, found.SegmentIndex);
    }

    [Theory]
    [InlineData("""Text="{Binding Name, Mo|de=TwoWay}" """)]         // another argument
    [InlineData("""Text="{DynamicResource App|Brush}" """)]          // not a binding
    [InlineData("""Te|xt="{Binding Name}" """)]                      // the attribute name
    [InlineData("""<Button Con|tent="x" />""")]                      // no markup extension
    public void Is_null_off_the_path(string text) => Assert.Null(At(text));

    [Fact]
    public void Reports_where_the_segment_is()
    {
        const string text = """ItemsSource="{Binding Run.StartupProjects}" """;
        var found = BindingPathLocator.Find(text, text.IndexOf("Startup", System.StringComparison.Ordinal) + 2);

        Assert.NotNull(found);
        Assert.Equal("StartupProjects", text.Substring(found.SegmentStart, found.SegmentLength));
    }
}
