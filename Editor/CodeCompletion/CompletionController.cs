using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AXamlIDE.Editor.Hover;
using AXamlIDE.Editor.Snippets;
using AXamlIDE.Models;
using AXamlIDE.Services.Lsp;
using static AXamlIDE.Editor.NameChars;

namespace AXamlIDE.Editor.CodeCompletion;

/// <summary>
/// Completion popup. AXAML uses the built-in XAML completion engine; other languages ask the language server.
/// </summary>
public sealed class CompletionController
{
    private readonly EditorContext _ctx;
    private readonly HoverController _hover;
    private readonly SnippetController _snippets;

    private CompletionWindow? _completionWindow;
    private CompletionWindow? _incompleteWindow;   // the open list, when the server left items out of it
    private int _completionSeq;

    public CompletionController(EditorContext ctx, HoverController hover, SnippetController snippets)
    {
        _ctx = ctx;
        _hover = hover;
        _snippets = snippets;

        ctx.Detaching += _ => Close();
    }

    public bool IsOpen => _completionWindow is not null;

    /// <summary>
    /// The open list is incomplete: the server left items out (there were too many, or some were still loading), so
    /// typing more of the word should ask again (<see cref="ShowCompletionAsync"/> then refills the open list).
    /// </summary>
    public bool IsIncomplete => _incompleteWindow is not null && ReferenceEquals(_incompleteWindow, _completionWindow);

    public void Close() => _completionWindow?.Close();

    // Typing something that isn't part of a name (=, space, >, ...) commits the selected item.
    // Called from TypingController (TextArea.TextEntering).
    public void HandleTextEntering(TextInputEventArgs e)
    {
        if (_completionWindow is not { } window || string.IsNullOrEmpty(e.Text) || IsNameChar(e.Text[0]) || e.Text == "}")
            return;

        // A snippet only goes in with Tab or Enter, as in Visual Studio: typing anything else just carries on typing.
        if (window.CompletionList.SelectedItem is SnippetCompletionData)
            window.Close();
        else
            window.CompletionList.RequestInsertion(e);
    }

    public async Task ShowCompletionAsync(OpenDocument doc, string? triggerChar)
    {
        var editor = _ctx.Editor;
        var seq = ++_completionSeq;
        var caret = editor.CaretOffset;

        if (doc.LanguageId == LanguageIds.Axaml)
        {
            var requestedText = doc.Document.Text;
            try
            {
                var result = await _ctx.Xaml.GetCompletionsAsync(doc.Path, requestedText, caret);
                if (result is { Completions.Count: > 0 }
                    && seq == _completionSeq
                    && ReferenceEquals(doc, _ctx.Active)
                    && editor.CaretOffset == caret
                    && doc.Document.Text == requestedText)
                {
                    var engineStart = Math.Clamp(result.StartPosition, 0, caret);
                    var engineWindow = new CompletionWindow(editor.TextArea) { StartOffset = engineStart };
                    foreach (var completion in result.Completions)
                    {
                        Action? retrigger = completion.TriggerCompletionAfterInsert
                            ? () => Dispatcher.UIThread.Post(() =>
                            {
                                if (ReferenceEquals(doc, _ctx.Active))
                                    _ = ShowCompletionAsync(doc, triggerChar: null);
                            })
                            : null;
                        engineWindow.CompletionList.CompletionData.Add(
                            new XamlEngineCompletionData(completion, retrigger));
                    }

                    AddSnippets(engineWindow, doc, engineStart);
                    ShowCompletionWindow(engineWindow, doc.Document, engineStart, caret);
                    // "{" in an attribute value: Binding is by far the most common, so Tab takes it
                    if (triggerChar == "{"
                        && engineWindow.CompletionList.CompletionData.FirstOrDefault(d => d.Text == "Binding") is { } binding)
                        engineWindow.CompletionList.SelectedItem = binding;

                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AXAML completion] {ex}");
                _ctx.Status($"AXAML completion unavailable: {ex.Message}");
            }
        }

        if (_ctx.Languages.ConnectionFor(doc) is not { Client: var client, Sync: var sync })
        {
            ShowSnippetsOnly(doc, seq, caret);   // AXAML text between elements: no engine items, but "grid" + Tab works here
            return;
        }

        sync.Flush(); // the server has to see what's on screen
        var loc = doc.Document.GetLocation(caret);
        LspCompletionList list;
        try
        {
            list = await client.CompletionAsync(doc.Uri, loc.Line - 1, loc.Column - 1, triggerChar);
        }
        catch (Exception)
        {
            return; // server error or exited: just no popup
        }

        // Nothing to show, a newer request superseded this one, the tab changed, or the caret went backwards.
        if (seq != _completionSeq || !ReferenceEquals(doc, _ctx.Active) || editor.CaretOffset < caret)
            return;
        var items = list.Items;
        if (items.Count == 0)
        {
            //ShowSnippetsOnly(doc, seq, caret);
            return;
        }

        // The server knows where the word being completed starts; otherwise walk back over name characters.
        var start = items[0].Edit is { } first
            ? doc.OffsetOf(first.Range.StartLine, first.Range.StartChar)
            : WordStart(doc.Document, caret);
        start = Math.Min(start, caret);

        var rank = 0;
        var data = new List<ICompletionData>(items.Count);
        foreach (var item in items.OrderBy(i => i.SortText ?? i.Label, StringComparer.Ordinal))
        {
            var editStart = item.Edit is { } edit
                ? doc.OffsetOf(edit.Range.StartLine, edit.Range.StartChar)
                : start;
            data.Add(new LspCompletionData(item, editStart, priority: -rank++, client, doc.Path));
        }

        // Asked again because the open list was incomplete: refill it rather than open another (no flicker, and the
        // selection stays on the item the typing matches).
        if (IsIncomplete && _completionWindow is { } open && open.StartOffset == start)
        {
            Refill(open, data, doc, start);
            if (!list.IsIncomplete)
                _incompleteWindow = null;
            return;
        }

        var window = new CompletionWindow(editor.TextArea) { StartOffset = start };
        foreach (var item in data)
            window.CompletionList.CompletionData.Add(item);
        AddSnippets(window, doc, start);

        // The selected item is resolved (its documentation, the using it adds) while the user looks at it.
        window.CompletionList.SelectionChanged += (_, _) => (window.CompletionList.SelectedItem as LspCompletionData)?.Resolve();

        ShowCompletionWindow(window, doc.Document, start, caret);
        _incompleteWindow = list.IsIncomplete ? window : null;
    }

    /// <summary>Swap the open list's items for a new answer, keeping the filter (the word typed so far).</summary>
    private void Refill(CompletionWindow window, IReadOnlyList<ICompletionData> data, OpenDocument doc, int start)
    {
        var completionList = window.CompletionList;
        completionList.CompletionData.Clear();
        foreach (var item in data)
            completionList.CompletionData.Add(item);
        AddSnippets(window, doc, start);

        // SelectItem does nothing for the text it last filtered by, so clear the filter first.
        var caret = _ctx.Editor.CaretOffset;
        completionList.SelectItem(string.Empty);
        if (caret > start)
            completionList.SelectItem(doc.Document.GetText(start, caret - start));
    }

    /// <summary>The language's snippets go in every list (below the server's items; see SnippetCompletionData).</summary>
    private void AddSnippets(CompletionWindow window, OpenDocument doc, int start)
    {
        foreach (var snippet in _snippets.CompletionItems(doc, start))
            window.CompletionList.CompletionData.Add(snippet);
    }

    /// <summary>No other items: a list of just the snippets whose prefix starts with the word being typed, if any.</summary>
    private void ShowSnippetsOnly(OpenDocument doc, int seq, int caret)
    {
        var start = WordStart(doc.Document, caret);
        if (seq != _completionSeq || start == caret)
            return;
        var typed = doc.Document.GetText(start, caret - start);
        var matches = _snippets.CompletionItems(doc, start)
            .Where(snippet => snippet.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            return;
        var window = new CompletionWindow(_ctx.Editor.TextArea) { StartOffset = start };
        foreach (var snippet in matches)
            window.CompletionList.CompletionData.Add(snippet);

        // Gone once the word stops matching ("s" offers svm, "string" nothing): an empty list would swallow Tab and Enter.
        void OnTyped(object? sender, TextInputEventArgs e)
        {
            var end = _ctx.Editor.CaretOffset;
            var word = end > start ? doc.Document.GetText(start, end - start) : "";
            if (!matches.Any(snippet => snippet.Text.StartsWith(word, StringComparison.OrdinalIgnoreCase)))
                window.Close();
        }
        _ctx.Editor.TextArea.TextEntered += OnTyped;
        window.Closed += (_, _) => _ctx.Editor.TextArea.TextEntered -= OnTyped;
        ShowCompletionWindow(window, doc.Document, start, caret);
    }

    private void ShowCompletionWindow(CompletionWindow window, TextDocument document, int start, int caret)
    {
        _completionWindow?.Close();
        _hover.Hide(resetOffset: true);

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completionWindow, window)) _completionWindow = null;
        };

        _completionWindow = window;
        window.Show();

        // Anything already typed after the word start becomes the initial filter.
        if (caret > start)
            window.CompletionList.SelectItem(document.GetText(start, caret - start));
    }
}
