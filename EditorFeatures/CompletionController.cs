using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AXamlIDE.Lsp;
using AXamlIDE.Models;
using static AXamlIDE.Helpers.NameChars;

namespace AXamlIDE.EditorFeatures
{
    /// <summary>
    /// Completion popup (AXAML engine + LSP). Replaces MainWindow's _completionWindow,
    /// _completionSeq, OnTextEntering, ShowCompletionAsync and ShowCompletionWindow.
    /// </summary>
    public sealed class CompletionController
    {
        private readonly EditorContext _ctx;
        private readonly HoverController _hover;

        private CompletionWindow? _completionWindow;
        private int _completionSeq;

        public CompletionController(EditorContext ctx, HoverController hover)
        {
            _ctx = ctx;
            _hover = hover;

            // Was in OnTabSelectionChanged.
            ctx.Detaching += _ => Close();
        }

        public bool IsOpen => _completionWindow is not null;

        public void Close() => _completionWindow?.Close();

        // Typing something that isn't part of a name (=, space, >, ...) commits the selected item.
        // Called from TypingController (TextArea.TextEntering).
        public void HandleTextEntering(TextInputEventArgs e)
        {
            if (_completionWindow is { } window && !string.IsNullOrEmpty(e.Text) && !IsNameChar(e.Text[0]))
                window.CompletionList.RequestInsertion(e);
        }

        public async Task ShowCompletionAsync(OpenDocument doc, string? triggerChar)
        {
            var editor = _ctx.Editor;
            var seq = ++_completionSeq;
            var caret = editor.CaretOffset;

            if (doc.LanguageId == "axaml")
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

                        ShowCompletionWindow(engineWindow, doc.Document, engineStart, caret);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AXAML completion] {ex}");
                    _ctx.Status($"AXAML completion unavailable: {ex.Message}");
                }
            }

            if (doc.Client is not { } client || doc.Sync is null) return;

            doc.Sync.Flush(); // the server has to see what's on screen
            var loc = doc.Document.GetLocation(caret);
            IReadOnlyList<LspCompletionItem> items;
            try
            {
                items = await client.CompletionAsync(doc.Uri, loc.Line - 1, loc.Column - 1, triggerChar);
            }
            catch (Exception)
            {
                return; // server error or exited: just no popup
            }

            // Nothing to show, a newer request superseded this one, the tab changed, or the caret went backwards.
            if (items.Count == 0 || seq != _completionSeq
                                 || !ReferenceEquals(doc, _ctx.Active) || editor.CaretOffset < caret)
                return;

            // The server knows where the word being completed starts; otherwise walk back over name characters.
            var start = items[0].Edit is { } first
                ? doc.OffsetOf(first.Range.StartLine, first.Range.StartChar)
                : WordStart(doc.Document, caret);
            start = Math.Min(start, caret);

            var window = new CompletionWindow(editor.TextArea) { StartOffset = start };

            var rank = 0;
            foreach (var item in items.OrderBy(i => i.SortText ?? i.Label, StringComparer.Ordinal))
            {
                var editStart = item.Edit is { } edit
                    ? doc.OffsetOf(edit.Range.StartLine, edit.Range.StartChar)
                    : start;
                window.CompletionList.CompletionData.Add(new LspCompletionData(item, editStart, priority: -rank++));
            }

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
}
