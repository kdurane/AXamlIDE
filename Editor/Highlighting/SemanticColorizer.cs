using System;
using System.Collections.Generic;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>A coloured stretch of a name; it moves with edits until the colours are fetched again.</summary>
internal sealed class SemanticColorSegment(IBrush brush) : TextSegment
{
    public IBrush Brush { get; } = brush;
}

/// <summary>
/// Draws the semantic colours (<see cref="SemanticHighlightController"/>) over TextMate's. It must come after TextMate's
/// colouriser in the text view's LineTransformers, so its colour is the one that shows.
/// </summary>
internal sealed class SemanticColorizer : DocumentColorizingTransformer
{
    private TextSegmentCollection<SemanticColorSegment>? _segments;
    private TextDocument? _document;

    /// <summary>Replace the colours. They're kept in a collection tied to the document, so edits move them along.</summary>
    public void Set(TextDocument document, IEnumerable<SemanticColorSegment> segments)
    {
        if (!ReferenceEquals(document, _document) || _segments is null)
        {
            _document = document;
            _segments = new TextSegmentCollection<SemanticColorSegment>(document);
        }
        else
        {
            _segments.Clear();
        }

        foreach (var segment in segments)
            _segments.Add(segment);
    }

    public void Clear() => _segments?.Clear();

    public bool IsEmpty => _segments is not { Count: > 0 };

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_segments is null || !ReferenceEquals(CurrentContext.Document, _document))
            return;

        foreach (var segment in _segments.FindOverlappingSegments(line.Offset, line.Length))
        {
            var start = Math.Max(segment.StartOffset, line.Offset);
            var end = Math.Min(segment.EndOffset, line.EndOffset);
            if (end > start)
                ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(segment.Brush));
        }
    }
}
