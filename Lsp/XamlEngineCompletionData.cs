using System;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AXaxmlIDE.CompletionEngine.Completion;

namespace AXaxmlIDE.Lsp;

public sealed class XamlEngineCompletionData(Completion completion, Action? triggerCompletion = null) : ICompletionData
{
    public IImage? Image => null;
    public string Text => completion.DisplayText;
    public object Content => completion.DisplayText;
    public object Description => completion.Description;
    public double Priority => -completion.Priority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var start = completionSegment.Offset;
        textArea.Document.Replace(start, completionSegment.Length, completion.InsertText);

        var caret = start + completion.InsertText.Length;
        if (completion.DeleteTextOffset is { } deleteOffset)
        {
            var deleteStart = caret + deleteOffset;
            var deleteLength = Math.Abs(deleteOffset);
            if (deleteLength > 0 && deleteStart >= 0 && deleteStart + deleteLength <= textArea.Document.TextLength)
            {
                textArea.Document.Remove(deleteStart, deleteLength);
                if (deleteOffset < 0)
                    caret = deleteStart;
            }
        }

        if (completion.RecommendedCursorOffset is { } cursorOffset)
            caret = start + Math.Clamp(cursorOffset, 0, completion.InsertText.Length);

        if (ShouldInsertElementPair(textArea, start, insertionRequestEventArgs))
        {
            var closingTag = $"</{completion.InsertText}>";
            textArea.Document.Insert(caret, ">" + closingTag);
            caret++;
        }

        textArea.Caret.Offset = Math.Clamp(caret, 0, textArea.Document.TextLength);
        if (completion.TriggerCompletionAfterInsert)
            triggerCompletion?.Invoke();
    }

    private bool ShouldInsertElementPair(TextArea textArea, int start, EventArgs insertionRequestEventArgs)
        => completion.Kind == CompletionKind.Class
           && completion.RecommendedCursorOffset is null
           && string.Equals(completion.InsertText, completion.DisplayText, StringComparison.Ordinal)
           && completion.InsertText.Length > 0
           && completion.InsertText[0] is not ('/' or '!')
           && (start == 0 || textArea.Document.GetCharAt(start - 1) == '<')
           && insertionRequestEventArgs is not TextInputEventArgs { Text: ">" };
}
