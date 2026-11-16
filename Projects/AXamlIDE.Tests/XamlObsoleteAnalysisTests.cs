using System.Linq;
using AXamlIDE.CompletionEngine.AssemblyMetadata;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Tests;

public sealed class XamlObsoleteAnalysisTests(AvaloniaMetadataFixture fixture) : IClassFixture<AvaloniaMetadataFixture>
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:local="using:App.Controls">

        """;

    /// <summary>The flagged text, by what each diagnostic's range covers.</summary>
    private static string[] Flagged(Metadata metadata, string body)
    {
        var text = Header + body + "\n</UserControl>";
        var lines = text.Split('\n');
        return [.. XamlObsoleteAnalysis.Find(metadata, text, "App")
            .Select(d => lines[d.StartLine][d.StartChar..d.EndChar])];
    }

    [Fact]
    public void Avalonia_12_TextBox_Watermark_is_flagged_with_the_attribute_message()
    {
        var text = Header + """<TextBox Watermark="Search" PlaceholderText="Search" />""" + "\n</UserControl>";

        var diagnostic = Assert.Single(XamlObsoleteAnalysis.Find(fixture.Metadata, text, "App"));
        Assert.Equal(LspDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("'TextBox.Watermark' is obsolete: 'Use PlaceholderText instead.'", diagnostic.Message);
        Assert.True(LspHelpers.HasDiagnosticTag(diagnostic.Raw, LspDiagnosticTag.Deprecated));
    }

    [Fact]
    public void Property_elements_are_flagged_too()
        => Assert.Equal(["TextBox.Watermark"],
            Flagged(fixture.Metadata, "<TextBox><TextBox.Watermark>Search</TextBox.Watermark></TextBox>"));

    [Fact]
    public void Current_properties_are_not_flagged()
        => Assert.Empty(Flagged(fixture.Metadata, """<TextBox PlaceholderText="Search" Grid.Row="1" x:Name="Box" />"""));

    [Fact]
    public void Obsolete_types_attached_properties_and_error_severity()
    {
        var metadata = new Metadata();
        var old = new MetadataType("OldCard") { FullName = "App.Controls.OldCard", Obsolete = new MetadataObsolete("Use Card.", IsError: false) };
        var help = new MetadataType("Help") { FullName = "App.Controls.Help" };
        help.Properties.Add(new MetadataProperty("Text", null, help, IsAttached: true, IsStatic: false, HasGetter: true, HasSetter: true)
        {
            Obsolete = new MetadataObsolete("", IsError: true)
        });
        metadata.AddType("using:App.Controls", old);
        metadata.AddType("using:App.Controls", help);

        var text = Header + """<local:OldCard local:Help.Text="Hi" />""" + "\n</UserControl>";
        var found = XamlObsoleteAnalysis.Find(metadata, text, "App");

        Assert.Equal(["'OldCard' is obsolete: 'Use Card.'", "'Help.Text' is obsolete"], found.Select(d => d.Message));
        Assert.Equal([LspDiagnosticSeverity.Warning, LspDiagnosticSeverity.Error], found.Select(d => d.Severity));
    }
}
