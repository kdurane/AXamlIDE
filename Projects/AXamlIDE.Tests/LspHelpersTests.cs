using System.IO;
using System.Text.Json;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Tests;

public class LspHelpersTests
{
    [Fact]
    public void Ranges_that_touch_overlap()
        => Assert.True(LspHelpers.RangesOverlap(new LspRange(1, 0, 1, 5), new LspRange(1, 5, 1, 9)));

    [Fact]
    public void Ranges_on_different_lines_do_not_overlap()
        => Assert.False(LspHelpers.RangesOverlap(new LspRange(1, 0, 1, 5), new LspRange(2, 0, 2, 5)));

    [Fact]
    public void ParseWorkspaceEdit_reads_changes_keyed_by_uri()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "a.cs"));
        var uri = new System.Uri(path).AbsoluteUri;
        using var json = JsonDocument.Parse($$"""
            { "changes": { "{{uri}}": [
                { "range": { "start": { "line": 1, "character": 2 }, "end": { "line": 1, "character": 6 } }, "newText": "Name" }
            ] } }
            """);

        var edits = LspHelpers.ParseWorkspaceEdit(json.RootElement);

        var (range, newText) = Assert.Single(edits[path]);
        Assert.Equal(new LspRange(1, 2, 1, 6), range);
        Assert.Equal("Name", newText);
    }

    [Fact]
    public void ParseWorkspaceEdit_reads_documentChanges()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "b.cs"));
        var uri = new System.Uri(path).AbsoluteUri;
        using var json = JsonDocument.Parse($$"""
            { "documentChanges": [ { "textDocument": { "uri": "{{uri}}", "version": 3 }, "edits": [
                { "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 0 } }, "newText": "// hi\n" }
            ] } ] }
            """);

        var edits = LspHelpers.ParseWorkspaceEdit(json.RootElement);

        Assert.Equal("// hi\n", Assert.Single(edits[path]).NewText);
    }
}
