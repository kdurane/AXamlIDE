using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace AXamlIDE.Views.Editor.Highlighting;

public sealed class OccurrenceRenderer : IBackgroundRenderer
{
    private static readonly IBrush HighlightBrush =
        new SolidColorBrush(Color.FromArgb(80, 255, 196, 0));

    private readonly List<TextSegment> _segments = [];

    public KnownLayer Layer => KnownLayer.Selection;

    public void Update(TextDocument? document, string? word)
    {
        _segments.Clear();
        if (document is null || string.IsNullOrEmpty(word) || !IsIdentifier(word))
            return;

        var text = document.Text;
        var offset = 0;
        while ((offset = text.IndexOf(word, offset, System.StringComparison.Ordinal)) >= 0)
        {
            var end = offset + word.Length;
            if ((offset == 0 || !IsIdentifierCharacter(text[offset - 1]))
                && (end == text.Length || !IsIdentifierCharacter(text[end])))
            {
                _segments.Add(new TextSegment { StartOffset = offset, Length = word.Length });
            }

            offset = end;
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_segments.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
            return;

        var firstOffset = textView.VisualLines.First().FirstDocumentLine.Offset;
        var lastLine = textView.VisualLines.Last().LastDocumentLine;
        var viewLength = lastLine.EndOffset - firstOffset;

        foreach (var segment in _segments)
        {
            if (segment.EndOffset <= firstOffset || segment.StartOffset >= firstOffset + viewLength)
                continue;

            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                drawingContext.FillRectangle(HighlightBrush, rect);
        }
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
            return false;

        for (var i = 1; i < value.Length; i++)
            if (!IsIdentifierCharacter(value[i]))
                return false;

        return true;
    }

    private static bool IsIdentifierStart(char value)
        => char.IsLetter(value) || value == '_';

    private static bool IsIdentifierCharacter(char value)
        => char.IsLetterOrDigit(value) || value == '_';
}
