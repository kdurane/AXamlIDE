using System;
using System.Linq;
using Avalonia.Input;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AXamlIDE.Editor.CodeLens;
using AXamlIDE.Services.Git;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.GitChanges;

/// <summary>
/// The thin change bars between the line numbers and the folding arrows, as in VS Code, Visual Studio and Rider:
/// green for added lines, blue for changed ones, and a small red wedge where lines were deleted.
/// What to draw comes from <see cref="GitChangeController"/>. Colours: GitGutter*Brush
/// (editorGutter.addedBackground / modifiedBackground / deletedBackground theme keys).
/// </summary>
public sealed class GitChangeMargin : AbstractMargin
{
    private const double MarginWidth = 6;
    private const double BarLeft = 1;
    private const double BarWidth = 3;
    private const double WedgeHalfHeight = 3;

    private readonly GitChangeController _changes;

    public GitChangeMargin(GitChangeController changes)
    {
        _changes = changes;
        changes.Changed += InvalidateVisual;
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        oldTextView?.VisualLinesChanged -= OnVisualLinesChanged;
        newTextView?.VisualLinesChanged += OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>A change bar was clicked (the editor shows what it was, with Revert).</summary>
    public event Action<LineChange>? ChangeClicked;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        Cursor = ChangeUnder(e) is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        base.OnPointerMoved(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ChangeUnder(e) is { } change)
        {
            e.Handled = true;
            ChangeClicked?.Invoke(change);
        }
        base.OnPointerPressed(e);
    }

    /// <summary>The change on the line under the pointer (a deletion's wedge: the lines either side of it).</summary>
    private LineChange? ChangeUnder(PointerEventArgs e)
    {
        if (TextView is not { VisualLinesValid: true } textView)
            return null;
        var y = e.GetPosition(this).Y + textView.VerticalOffset;
        if (textView.GetVisualLineFromVisualTop(y) is not { } visualLine)
            return null;
        var line = visualLine.FirstDocumentLine.LineNumber;
        return _changes.ChangeAt(line)
               ?? _changes.Changes.Where(change => change.Kind == LineChangeKind.Deleted && change.FirstLine == line - 1)
                   .Cast<LineChange?>().FirstOrDefault();
    }

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    public override void Render(DrawingContext context)
    {
        if (TextView is not { VisualLinesValid: true, Document: { } document } textView || _changes.Changes.Count == 0)
            return;

        var visualLines = textView.VisualLines;
        var firstShown = visualLines[0].FirstDocumentLine.LineNumber;
        var lastShown = visualLines[^1].LastDocumentLine.LineNumber;

        foreach (var change in _changes.Changes)
        {
            if (change.Kind == LineChangeKind.Deleted)
            {
                // A wedge on the boundary below line FirstLine (at the top for 0).
                if (change.FirstLine < firstShown - 1 || change.FirstLine > lastShown)
                    continue;
                var y = change.FirstLine == 0
                    ? LineTop(textView, 1)
                    : LineTop(textView, change.FirstLine) + LineHeight(textView, change.FirstLine);
                if (ThemeBrushes.Get("GitGutterDeletedBrush") is { } deleted)
                    context.DrawGeometry(deleted, null, Wedge(y));
                continue;
            }

            var first = Math.Max(change.FirstLine, firstShown);
            var last = Math.Min(Math.Min(change.LastLine, lastShown), document.LineCount);
            if (first > last)
                continue;
            if (ThemeBrushes.Get(change.Kind == LineChangeKind.Added ? "GitGutterAddedBrush" : "GitGutterModifiedBrush") is not { } brush)
                continue;
            var top = LineTop(textView, first) + LensAbove(textView, first);   // the bar starts at the code, not its lens
            var bottom = LineTop(textView, last) + LineHeight(textView, last);
            context.FillRectangle(brush, new Rect(BarLeft, top, BarWidth, Math.Max(1, bottom - top)));
        }
    }

    /// <summary>A line's top in the margin (the editor's scroll taken off).</summary>
    private static double LineTop(TextView textView, int line)
        => textView.GetVisualTopByDocumentLine(line) - textView.VerticalOffset;

    private static double LensAbove(TextView textView, int line)
        => textView.GetVisualLine(line) is { } visualLine ? CodeLensSpace.Above(visualLine) : 0;

    private static double LineHeight(TextView textView, int line)
        => textView.GetVisualLine(line)?.Height ?? textView.DefaultLineHeight;

    private static StreamGeometry Wedge(double y)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(BarLeft, y - WedgeHalfHeight), true);
        g.LineTo(new Point(BarLeft + BarWidth + 1.5, y));
        g.LineTo(new Point(BarLeft, y + WedgeHalfHeight));
        g.EndFigure(true);
        return geometry;
    }
}
