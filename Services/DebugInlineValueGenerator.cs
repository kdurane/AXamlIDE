using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace AXamlIDE.Editor;

/// <summary>
/// Draws "name = value" hints at the end of lines within <see cref="Window"/> lines of the current debug line.
/// Add once per editor:  editor.TextArea.TextView.ElementGenerators.Add(generator);
/// After Show()/Clear() call editor.TextArea.TextView.Redraw().
/// </summary>
public sealed partial class DebugInlineValueGenerator : VisualLineElementGenerator
{
    private static readonly Regex Identifier = IdentifierRegex();

    private readonly Dictionary<int, string?> _cache = new();
    private IReadOnlyDictionary<string, string>? _values;
    private int _currentLine;

    public IBrush Brush { get; set; } = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x90));
    public int Window { get; set; } = 5;
    public int MaxHintsPerLine { get; set; } = 4;

    public void Show(IReadOnlyDictionary<string, string> values, int currentLine)
    {
        _values = values;
        _currentLine = currentLine;
        _cache.Clear();
    }

    public void Clear()
    {
        _values = null;
        _cache.Clear();
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var doc = CurrentContext.Document;
        var line = doc.GetLineByOffset(startOffset);
        return HintFor(doc, line.LineNumber) is null ? -1 : line.EndOffset;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var doc = CurrentContext.Document;
        var line = doc.GetLineByOffset(offset);
        if (offset != line.EndOffset || HintFor(doc, line.LineNumber) is not { } hint)
            return null;

        return new HintElement("    " + hint, Brush);
    }

    private string? HintFor(TextDocument doc, int lineNumber)
    {
        if (_values is null || Math.Abs(lineNumber - _currentLine) > Window)
            return null;
        if (_cache.TryGetValue(lineNumber, out var cached))
            return cached;

        var text = doc.GetText(doc.GetLineByNumber(lineNumber));
        var parts = new List<string>();
        var seen = new HashSet<string>();
        foreach (Match m in Identifier.Matches(text))
        {
            // `other.Count` must not pick up this.Count; `this.Count` is fine.
            if (m.Index > 0 && text[m.Index - 1] == '.' && !text.AsSpan(0, m.Index).EndsWith("this."))
                continue;
            if (_values.TryGetValue(m.Value, out var v) && seen.Add(m.Value))
            {
                parts.Add($"{m.Value} = {v}");
                if (parts.Count >= MaxHintsPerLine)
                    break;
            }
        }


        var result = parts.Count == 0 ? null : string.Join(", ", parts);
        _cache[lineNumber] = result;
        return result;
    }

    private sealed class HintElement(string text, IBrush brush) : FormattedTextElement(text, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            // TextRunProperties is only assigned after ConstructElement returns, so colour it here.
            TextRunProperties?.SetForegroundBrush(brush);
            return base.CreateTextRun(startVisualColumn, context);
        }
    }

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex IdentifierRegex();
}


