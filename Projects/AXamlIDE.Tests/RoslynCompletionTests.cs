using System.Text.Json;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Tests;

/// <summary>Completion items, semantic tokens, inlay hints and settings, from responses shaped like Roslyn 5.12's.</summary>
public class RoslynCompletionTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Resolved_import_completion_carries_its_using()
    {
        var item = LspClient.ParseCompletionItem(Json("""
            {
              "label": "StringBuilder", "labelDetails": { "description": "System.Text" }, "kind": 7,
              "documentation": { "kind": "plaintext", "value": "class System.Text.StringBuilder" },
              "textEdit": { "range": { "start": { "line": 13, "character": 8 }, "end": { "line": 13, "character": 15 } }, "newText": "StringBuilder" },
              "additionalTextEdits": [ { "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 0 } }, "newText": "using System.Text;\n\n" } ],
              "data": { "ResultId": 1 }
            }
            """));

        Assert.Equal("System.Text", item.LabelDescription);
        Assert.Equal("class System.Text.StringBuilder", item.Documentation);
        var edit = Assert.Single(item.AdditionalEdits!);
        Assert.Equal(new LspRange(0, 0, 0, 0), edit.Range);
        Assert.Equal("using System.Text;\n\n", edit.NewText);
        Assert.Equal(1, item.Raw!.Value.GetProperty("data").GetProperty("ResultId").GetInt32());
    }

    [Fact]
    public void Override_completion_command_is_a_complex_edit()
    {
        var item = LspClient.ParseCompletionItem(Json("""
            {
              "label": "Run(int count)", "kind": 2, "filterText": "Run",
              "textEdit": { "range": { "start": { "line": 15, "character": 13 }, "end": { "line": 15, "character": 13 } }, "newText": "" },
              "command": { "title": "CompleteComplexEditCommand", "command": "roslyn.client.completionComplexEdit", "arguments": [
                { "uri": "file:///p/Program.cs" },
                { "range": { "start": { "line": 15, "character": 4 }, "end": { "line": 15, "character": 13 } },
                  "newText": "public override void Run(int count)\n    {\n        base.Run(count);\n    }" },
                false, 413 ] }
            }
            """));

        var complex = RoslynCompletion.ComplexEdit(item.Command);

        Assert.NotNull(complex);
        Assert.Equal("file:///p/Program.cs", complex.Uri);
        Assert.Equal(new LspRange(15, 4, 15, 13), complex.Edit.Range);
        Assert.StartsWith("public override void Run", complex.Edit.NewText);
        Assert.False(complex.IsSnippet);
        Assert.Equal(413, complex.CaretOffset);
    }

    [Fact]
    public void Other_commands_are_not_complex_edits()
    {
        var command = Json("""{ "title": "x", "command": "editor.action.triggerParameterHints" }""");

        Assert.Null(RoslynCompletion.ComplexEdit(command));
        Assert.Equal("editor.action.triggerParameterHints", RoslynCompletion.CommandName(command));
        Assert.Null(RoslynCompletion.ComplexEdit(null));
    }

    [Fact]
    public void Semantic_tokens_are_relative_to_the_one_before()
    {
        string[] legend = ["namespace", "type", "class", "parameter"];
        // line 9 col 8 len 3 type 1; same line +4 len 1 type 3; next line col 8 len 7 type 2; type 99 is skipped
        int[] data = [9, 8, 3, 1, 0, 0, 4, 1, 3, 1, 1, 8, 7, 2, 0, 0, 9, 2, 99, 0];

        var tokens = SemanticTokens.Decode(data, legend);

        Assert.Equal(
        [
            new LspSemanticToken(9, 8, 3, "type", 0),
            new LspSemanticToken(9, 12, 1, "parameter", 1),
            new LspSemanticToken(10, 8, 7, "class", 0)
        ], tokens);
    }

    [Fact]
    public void Inlay_hint_label_parts_are_joined()
    {
        var plain = LspClient.ParseInlayHint(Json("""
            { "position": { "line": 10, "character": 14 }, "label": "count:", "kind": 2, "paddingRight": true }
            """));
        var parts = LspClient.ParseInlayHint(Json("""
            { "position": { "line": 9, "character": 12 }, "label": [ { "value": "List" }, { "value": "<int>" } ] }
            """));

        Assert.Equal(new LspInlayHint(10, 14, "count:", PaddingLeft: false, PaddingRight: true), plain);
        Assert.Equal("List<int>", parts!.Label);
        Assert.Null(LspClient.ParseInlayHint(Json("""{ "position": { "line": 1, "character": 0 }, "label": "" }""")));
    }

    [Fact]
    public void Settings_answer_csharp_options_from_the_users_settings()
    {
        var defaults = new AXamlIDE.Models.EditorFeatureSettings();
        var noHints = defaults with { InlayParameterHints = false, InlayTypeHints = false };

        Assert.Equal(true, RoslynSettings.Get("csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters", defaults));
        Assert.Equal(false, RoslynSettings.Get("csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters", noHints));
        Assert.Equal(false, RoslynSettings.Get("csharp|inlay_hints.csharp_enable_inlay_hints_for_types", noHints));
        Assert.Equal(true, RoslynSettings.Get("csharp|inlay_hints.dotnet_enable_inlay_hints_for_literal_parameters", noHints));   // which ones, when on
        Assert.Equal(true, RoslynSettings.Get("csharp|completion.dotnet_show_completion_items_from_unimported_namespaces", defaults));
        Assert.Null(RoslynSettings.Get("visual_basic|inlay_hints.dotnet_enable_inlay_hints_for_parameters", defaults));
        Assert.Null(RoslynSettings.Get("csharp|code_style.formatting.indentation_and_spacing.tab_width", defaults));   // Roslyn's default
    }
}
