using System;
using System.Collections.Generic;
using System.Linq;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AXamlIDE.Services.ColorValues;

namespace AXamlIDE.Editor.ColorSwatches;

/// <summary>
/// Puts a <see cref="ColorSwatch"/> before every colour in the text (VS Code's colour decorators). The colours
/// come from <see cref="Find"/> (Core's ColorLiterals, for the document's language); null turns swatches off.
/// One line is looked at a time, and its colours are kept until its text changes.
/// </summary>
internal sealed class ColorSwatchGenerator(Func<DocumentLine, ColorLiteral, ColorSwatch> createSwatch) : VisualLineElementGenerator
{
    private DocumentLine? _cachedLine;
    private string? _cachedText;
    private IReadOnlyList<ColorLiteral> _cachedColors = [];

    /// <summary>Finds the colours in a line of text; null when the document's language has none.</summary>
    public Func<string, IReadOnlyList<ColorLiteral>>? Find
    {
        get;
        set
        {
            field = value;
            _cachedLine = null;
        }
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (Find is null)
            return -1;
        var line = CurrentContext.Document.GetLineByOffset(startOffset);
        var column = startOffset - line.Offset;
        foreach (var color in ColorsIn(line))
            if (color.Start >= column)
                return line.Offset + color.Start;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.Document.GetLineByOffset(offset);
        var color = ColorsIn(line).FirstOrDefault(color => line.Offset + color.Start == offset);
        return color is null ? null : new InlineObjectElement(0, createSwatch(line, color));
    }

    /// <summary>The colour starting at <paramref name="column"/> of <paramref name="line"/> as the text is now.</summary>
    public ColorLiteral? ColorAt(TextDocument document, DocumentLine line, int column)
        => Find is null || line.IsDeleted ? null : Find(document.GetText(line)).FirstOrDefault(color => color.Start == column);

    private IReadOnlyList<ColorLiteral> ColorsIn(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        if (!ReferenceEquals(line, _cachedLine) || text != _cachedText)
        {
            _cachedLine = line;
            _cachedText = text;
            _cachedColors = Find!(text);
        }
        return _cachedColors;
    }
}
