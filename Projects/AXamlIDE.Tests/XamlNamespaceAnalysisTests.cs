using System.Linq;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Tests;

public sealed class XamlNamespaceAnalysisTests
{
    private const string Root = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        """;

    private static string[] Unused(string text)
        => XamlNamespaceAnalysis.Find(text)
            .Where(d => d.Severity == LspDiagnosticSeverity.Hint)
            .Select(d => d.Message.Split('\'')[1])
            .ToArray();

    [Fact]
    public void Ignorable_prefix_with_no_declaration_is_flagged_on_the_prefix()
    {
        var text = Root + """
                         mc:Ignorable="d" x:Class="A.B">
            </UserControl>
            """;

        var diagnostic = Assert.Single(XamlNamespaceAnalysis.Find(text), d => d.Severity == LspDiagnosticSeverity.Warning);
        Assert.Contains("'d'", diagnostic.Message);
        var line = text.Split('\n')[diagnostic.StartLine];
        Assert.Equal("d", line[diagnostic.StartChar..diagnostic.EndChar]);
    }

    [Fact]
    public void Declared_ignorable_prefix_is_fine_and_counts_mc_as_used()
    {
        var text = Root + """
                         xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                         mc:Ignorable="d" d:DesignWidth="800" x:Class="A.B">
            </UserControl>
            """;

        Assert.Empty(XamlNamespaceAnalysis.Find(text));
    }

    [Fact]
    public void A_prefix_named_only_in_Ignorable_is_unused()
    {
        var text = Root + """
                         xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                         mc:Ignorable="d" x:Class="A.B">
            </UserControl>
            """;

        Assert.Equal(["xmlns:d"], Unused(text));
    }

    [Fact]
    public void Uses_in_values_selectors_and_markup_extensions_count()
    {
        var text = Root + """
                         xmlns:vm="using:App.ViewModels"
                         xmlns:local="using:App.Controls"
                         xmlns:conv="using:App.Converters"
                         xmlns:attached="using:App.Attached"
                         mc:Ignorable="" x:DataType="vm:Main">
                <UserControl.Styles>
                    <Style Selector="local|Card" />
                </UserControl.Styles>
                <TextBlock Text="{Binding Name, Converter={x:Static conv:Converters.Upper}}"
                           Tag="{Binding (attached:Help.Text)}" />
            </UserControl>
            """;

        Assert.Empty(Unused(text));
    }

    [Fact]
    public void A_prefix_used_only_in_a_comment_is_unused()
    {
        var text = Root + """
                         xmlns:local="using:App.Controls"
                         mc:Ignorable="" x:Class="A.B">
                <!-- <local:Card /> -->
            </UserControl>
            """;

        Assert.Equal(["xmlns:local"], Unused(text));
    }

    [Fact]
    public void The_unused_declaration_is_faded_as_a_whole()
    {
        var text = Root + """
                         xmlns:local="using:App.Controls"
                         mc:Ignorable="" x:Class="A.B" />
            """;

        var diagnostic = Assert.Single(XamlNamespaceAnalysis.Find(text));
        Assert.True(LspHelpers.HasDiagnosticTag(diagnostic.Raw, LspDiagnosticTag.Unnecessary));
        var line = text.Split('\n')[diagnostic.StartLine];
        Assert.Equal("xmlns:local=\"using:App.Controls\"", line[diagnostic.StartChar..diagnostic.EndChar]);
    }

    [Fact]
    public void Element_and_attribute_prefixes_need_a_declaration()
    {
        var text = Root + """
                         mc:Ignorable="" x:Class="A.B" d:DesignWidth="800">
                <local:Card />
            </UserControl>
            """;

        var errors = XamlNamespaceAnalysis.Find(text).Where(d => d.Severity == LspDiagnosticSeverity.Error)
            .Select(d => d.Message.Split('\'')[1]);
        Assert.Equal(["d", "local"], errors.Order());
    }

    [Fact]
    public void Half_typed_text_does_not_throw()
    {
        var text = Root + """
                         xmlns:local="using:App
            """;

        _ = XamlNamespaceAnalysis.Find(text);
    }
}
