using System;
using System.Collections.Generic;
using System.Linq;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.InlayHints;

/// <summary>An inlay hint at its place in the text (no length); it moves with edits until the hints are fetched again.</summary>
internal sealed class InlayHintSegment(LspInlayHint hint) : TextSegment
{
    public LspInlayHint Hint { get; } = hint;
}

/// <summary>
/// Puts each inlay hint (<see cref="InlayHintControl"/>) into its line, before the character it belongs to. The hints
/// take up room in the line but aren't part of the text. Call <see cref="Set"/> and redraw the text view to change them.
/// </summary>
internal sealed class InlayHintGenerator(Func<IReadOnlyList<LspInlayHint>, InlayHintControl> createControl) : VisualLineElementGenerator
{
    private TextSegmentCollection<InlayHintSegment>? _hints;
    private TextDocument? _document;

    /// <summary>Replace the hints. They're kept in a collection tied to the document, so edits move them along.</summary>
    public void Set(TextDocument document, IEnumerable<InlayHintSegment> hints)
    {
        if (!ReferenceEquals(document, _document) || _hints is null)
        {
            _document = document;
            _hints = new TextSegmentCollection<InlayHintSegment>(document);
        }
        else
        {
            _hints.Clear();
        }

        foreach (var hint in hints)
            _hints.Add(hint);
    }

    public void Clear() => _hints?.Clear();

    public bool IsEmpty => _hints is not { Count: > 0 };

    public override int GetFirstInterestedOffset(int startOffset)
        => ReferenceEquals(CurrentContext.Document, _document) && _hints?.FindFirstSegmentWithStartAfter(startOffset) is { } next
            ? next.StartOffset
            : -1;

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (_hints is null || !ReferenceEquals(CurrentContext.Document, _document))
            return null;
        // Two hints in one place (rare) share one control.
        var here = _hints.FindSegmentsContaining(offset).Where(segment => segment.StartOffset == offset).Select(segment => segment.Hint).ToList();
        return here.Count == 0 ? null : new InlineObjectElement(0, createControl(here));
    }
}
