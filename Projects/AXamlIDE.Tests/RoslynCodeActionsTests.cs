using System.Linq;
using System.Text.Json;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Tests;

/// <summary>Roslyn's "Suppress or configure issues" group and file-creating edits, from responses shaped like Roslyn 5.12's.</summary>
public class RoslynCodeActionsTests
{
    private const string Group = """
        {
          "title": "Suppress or configure issues", "kind": "quickfix",
          "command": { "command": "roslyn.client.nestedCodeAction", "arguments": [ {
            "UniqueIdentifier": "Suppress or configure issues",
            "NestedCodeActions": [
              { "title": "in Source", "kind": "quickfix",
                "data": { "UniqueIdentifier": "in Source", "CodeActionPath": [ "Suppress or configure issues", "Suppress CA1822", "in Source" ] } },
              { "title": "Fix All: in Source", "kind": "quickfix",
                "data": { "UniqueIdentifier": "Fix All: in Source", "FixAllFlavors": [ "Document", "Project" ],
                          "CodeActionPath": [ "Suppress or configure issues", "Suppress CA1822", "in Source" ] } },
              { "title": "Warning", "kind": "quickfix",
                "data": { "UniqueIdentifier": "Warning", "CodeActionPath": [ "Suppress or configure issues", "Configure CA1822 severity", "Warning" ] } }
            ] } ] }
        }
        """;

    private static LspCodeAction Parse(string json) => LspClient.ParseCodeAction(JsonDocument.Parse(json).RootElement)!;

    [Fact]
    public void Group_lists_its_actions_with_menu_paths()
    {
        var group = Parse(Group);
        var nested = RoslynCodeActions.Nested(group);

        Assert.Equal(3, nested.Count);
        Assert.Equal(["Suppress CA1822", "in Source"], RoslynCodeActions.MenuPath(nested[0], group.Title));
        Assert.Equal(["Configure CA1822 severity", "Warning"], RoslynCodeActions.MenuPath(nested[2], group.Title));
        Assert.Empty(RoslynCodeActions.FixAllFlavors(RoslynCodeActions.Data(nested[0])));
        Assert.Equal(["Document", "Project"], RoslynCodeActions.FixAllFlavors(RoslynCodeActions.Data(nested[1])));
    }

    [Fact]
    public void Ordinary_action_has_no_group()
        => Assert.Empty(RoslynCodeActions.Nested(Parse("""{ "title": "Make static", "kind": "quickfix" }""")));

    [Fact]
    public void Workspace_edit_that_creates_a_file()
    {
        using var json = JsonDocument.Parse("""
            { "documentChanges": [
                { "kind": "create", "uri": "file:///work/GlobalSuppressions.cs" },
                { "textDocument": { "uri": "file:///work/GlobalSuppressions.cs", "version": null },
                  "edits": [ { "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 0 } }, "newText": "// x" } ] }
            ] }
            """);

        Assert.Equal([System.IO.Path.GetFullPath("/work/GlobalSuppressions.cs")], LspHelpers.FilesToCreate(json.RootElement));
        Assert.Single(LspHelpers.ParseWorkspaceEdit(json.RootElement).Single().Value);
    }
}
