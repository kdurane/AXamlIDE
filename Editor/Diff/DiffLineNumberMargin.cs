using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Editor.Diff;

/// <summary>
/// Line numbers for one side of a diff: each line's number in its own file, and none beside filler lines
/// (the editor's own line numbers would count the filler too).
/// </summary>
public sealed class DiffLineNumberMargin : AbstractMargin
{
    private const double Padding = 8;

    /// <summary>One entry per document line.</summary>
    public IReadOnlyList<DiffLine> Lines
    {
        get;
        set
        {
            field = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    } = [];

    public IBrush? Foreground
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var largest = Lines.Max(line => line.Number) ?? 1;
        var digits = Math.Max(2, largest.ToString(CultureInfo.InvariantCulture).Length);
        return new Size(Format(new string('9', digits)).Width + Padding * 2, 0);
    }

    public override void Render(DrawingContext context)
    {
        if (TextView is not { VisualLinesValid: true } view)
            return;

        foreach (var line in view.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            if (index >= Lines.Count || Lines[index].Number is not { } number)
                continue;
            var text = Format(number.ToString(CultureInfo.InvariantCulture));
            context.DrawText(text, new Point(Bounds.Width - text.Width - Padding, line.VisualTop - view.VerticalOffset));
        }
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    private FormattedText Format(string text) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(TextView?.GetValue(TextBlock.FontFamilyProperty) ?? FontFamily.Default),
        TextView?.GetValue(TextBlock.FontSizeProperty) ?? 13,
        Foreground ?? Brushes.Gray);
}
