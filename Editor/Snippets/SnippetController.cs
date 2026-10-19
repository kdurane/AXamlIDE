using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Interactivity;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Snippets;
using static AXamlIDE.Editor.NameChars;

namespace AXamlIDE.Editor.Snippets;

/// <summary>
/// Code snippets in the editor, as in Visual Studio: a snippet's prefix shows in the completion list (picking it
/// inserts the snippet), typing a prefix and pressing Tab expands it, and Insert Snippet (Ctrl+J) picks one from a
/// list, around the selection if there is one. Snippets come from <see cref="SnippetLibrary"/>.
/// </summary>
public sealed class SnippetController
{
    private readonly EditorContext _ctx;
    private readonly SnippetLibrary _library;
    private readonly IDialogService _dialogs;

    public SnippetController(EditorContext ctx, SnippetLibrary library, IDialogService dialogs)
    {
        _ctx = ctx;
        _library = library;
        _dialogs = dialogs;
        // Tunnel: before the editor turns Tab into an indent.
        ctx.Editor.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Set by the pane: Tab doesn't expand while the completion list is open (Tab picks from it).</summary>
    public Func<bool> IsCompletionOpen { get; set; } = () => false;

    /// <summary>
    /// The snippets to offer in a completion list starting at <paramref name="start"/>: none after a '.', and in AXAML
    /// element snippets where an element can go, attribute ones (xmlns, x:DataType) inside a tag.
    /// </summary>
    public IEnumerable<SnippetCompletionData> CompletionItems(OpenDocument document, int start)
    {
        var text = document.Document;
        if (start > 0 && text.GetCharAt(start - 1) == '.')
            return [];
        var snippets = _library.For(document.LanguageId);
        if (document.LanguageId == LanguageIds.Axaml)
        {
            var element = start > 0 && text.GetCharAt(start - 1) == '<' || !InsideTag(text.Text, start);
            snippets = [.. snippets.Where(snippet => snippet.Body.StartsWith('<') == element)];
        }
        return snippets.Select(snippet => new SnippetCompletionData(snippet, document.Path));
    }

    /// <summary>Insert Snippet: pick from the language's snippets; inserted at the caret (around the selection).</summary>
    public async Task InsertFromListAsync()
    {
        if (_ctx.Active is not { } document)
            return;
        var snippets = _library.For(document.LanguageId);
        if (snippets.Count == 0)
        {
            _ctx.Status($"There are no snippets for {document.LanguageId} files (Tools > Edit Snippets... adds your own).");
            return;
        }
        var labels = snippets.Select(snippet => $"{snippet.Prefix}  —  {snippet.Name}").ToList();
        if (await _dialogs.PickAsync("Insert Snippet", "Snippet", labels) is not { } picked
            || !ReferenceEquals(document, _ctx.Active))
            return;
        var chosen = snippets[labels.IndexOf(picked)];
        _ctx.Editor.TextArea.Focus();
        SnippetInserter.Insert(_ctx.Editor.TextArea, chosen, _ctx.Editor.CaretOffset, document.Path);
    }

    /// <summary>Tab right after a snippet's prefix ("prop|") expands it.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None || _ctx.Active is not { } document
            || IsCompletionOpen() || _ctx.Editor.SelectionLength > 0
            || !_ctx.Editor.TextArea.StackedInputHandlers.IsEmpty)   // a snippet being filled in: Tab moves between its fields
            return;

        var caret = _ctx.Editor.CaretOffset;
        var start = WordStart(document.Document, caret);
        if (start == caret)
            return;
        // In AXAML an element snippet can be typed after its '<' too ("<grid" + Tab).
        var word = document.Document.GetText(start, caret - start);
        if (_library.For(document.LanguageId).FirstOrDefault(snippet => snippet.Prefix == word) is not { } snippet)
            return;
        if (snippet.Body.StartsWith('<') && start > 0 && document.Document.GetCharAt(start - 1) == '<')
            start--;

        e.Handled = true;
        SnippetInserter.Insert(_ctx.Editor.TextArea, snippet, start, document.Path);
    }

    /// <summary>Between a '&lt;' and its '&gt;' (where attributes go), going back from <paramref name="offset"/>.</summary>
    private static bool InsideTag(string text, int offset)
    {
        for (var i = Math.Min(offset, text.Length) - 1; i >= 0; i--)
        {
            if (text[i] == '>')
                return false;
            if (text[i] == '<')
                return true;
        }
        return false;
    }
}
