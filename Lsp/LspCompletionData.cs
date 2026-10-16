using System;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace AXamlIDE.Lsp;

    /// <summary>Adapts one LSP CompletionItem to AvaloniaEdit's completion list.</summary>
    public sealed class LspCompletionData(LspCompletionItem item, int editStartOffset, double priority) : ICompletionData
    {
        // offset where the server wants the replacement to begin
        public IImage? Image => null;

        /// <summary>What the typed prefix is matched against.</summary>
        public string Text => item.FilterText ?? item.Label;

        /// <summary>What the list shows.</summary>
        public object Content => item.Label;

        /// <summary>Shown beside the list while the item is selected.</summary>
        public object? Description
        {
            get
            {
                if (item.Detail is null) return item.Documentation;
                return item.Documentation is null
                    ? item.Detail
                    : item.Detail + Environment.NewLine + Environment.NewLine + item.Documentation;
            }
        }

        public double Priority { get; } = priority;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var newText = item.Edit?.NewText ?? item.InsertText ?? item.Label;

            // Replace from where the server says the word starts up to the caret, which also
            // covers anything typed since the request went out. completionSegment is the
            // window's own view of that span, so it is the fallback.
            var start = item.Edit is not null && editStartOffset <= completionSegment.EndOffset
                ? editStartOffset
                : completionSegment.Offset;
            var length = Math.Max(0, completionSegment.EndOffset - start);

            textArea.Document.Replace(start, length, newText);
            textArea.Caret.Offset = start + newText.Length;
        }
    }