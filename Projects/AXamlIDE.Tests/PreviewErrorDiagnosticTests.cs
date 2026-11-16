using System.Xml;
using AXamlIDE.Services.Preview;

namespace AXamlIDE.Tests;

public sealed class PreviewErrorDiagnosticTests
{
    private const string Xaml = "<StackPanel>\r\n    <TextBox Text=\"a\"\r\n    <Button Content=\"Go\" />\r\n</StackPanel>";

    private static string Squiggled(PreviewError error, string text)
    {
        var diagnostic = PreviewErrorDiagnostic.From(error, text)!;
        Assert.Equal(diagnostic.StartLine, diagnostic.EndLine);
        return text.Split('\n')[diagnostic.StartLine][diagnostic.StartChar..diagnostic.EndChar];
    }

    [Fact]
    public void A_missing_end_of_tag_is_squiggled_where_the_XML_parser_stopped()
    {
        // What the designer reports comes from the same parser, so take its position from it.
        var ex = Assert.Throws<XmlException>(() => new XmlDocument().LoadXml(Xaml));
        var error = PreviewError.From(ex.Message, ex.LineNumber, ex.LinePosition);

        Assert.Equal("<", Squiggled(error, Xaml));
        Assert.True(PreviewErrorDiagnostic.IsFrom(PreviewErrorDiagnostic.From(error, Xaml)!.Raw));
    }

    [Fact]
    public void On_a_name_the_whole_name_is_squiggled()
        => Assert.Equal("TextBox", Squiggled(new PreviewError("Unknown", 2, 6), Xaml));

    [Fact]
    public void Without_a_column_the_line_is_squiggled_without_its_indent_or_CR()
        => Assert.Equal("<TextBox Text=\"a\"", Squiggled(new PreviewError("Bad", 2, null), Xaml));

    [Fact]
    public void Positions_past_the_end_are_clamped_and_no_line_means_no_squiggle()
    {
        Assert.Null(PreviewErrorDiagnostic.From(new PreviewError("Bad", null, null), Xaml));
        Assert.NotNull(PreviewErrorDiagnostic.From(new PreviewError("Bad", 99, 99), Xaml));
    }
}
