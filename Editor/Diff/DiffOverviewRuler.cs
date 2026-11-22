using System;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using AXamlIDE.Editor.Highlighting;
using AXamlIDE.Services.Git;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.Diff;

/// <summary>
/// The diff view's one vertical scrollbar, at its far right, with every change mapped on it (as VS Code's diff):
///
///   ▒   the thumb: the part on screen
///   ─   the caret line
///   ▌   lines removed from the left (old) side, in the left half
///    ▐  lines added on the right (new) side, in the right half; a changed line gets both
///
/// One strip does for both sides: they have the same lines (filler included) and scroll together.
/// Declared in DiffView.axaml, given the right-hand editor by <see cref="Attach"/>. Colours: DiffOverviewRemovedBrush
/// and DiffOverviewInsertedBrush (diffEditorOverview.* theme keys), plus the OverviewRuler* ones.
/// </summary>
public sealed class DiffOverviewRuler : EditorScrollRuler
{
    private SideBySideDiff? _diff;

    public void Attach(TextEditor editor)
    {
        AttachEditor(editor);
        editor.TextArea.Caret.PositionChanged += (_, _) => InvalidateVisual();
    }

    public SideBySideDiff? Diff
    {
        get => _diff;
        set
        {
            _diff = value;
            InvalidateVisual();
        }
    }

    protected override void RenderMarks(DrawingContext context, TextView textView, double scale)
    {
        if (_diff is not { } diff || Editor is not { } editor)
            return;
        var half = (Bounds.Width - 1) / 2;
        RenderRuns(context, textView, diff, left: true, 1, half, scale);
        RenderRuns(context, textView, diff, left: false, 1 + half, half, scale);
        RenderCaretLine(context, textView, editor.TextArea.Caret.Line, scale);
    }

    /// <summary>One mark per block of changed lines on one side (one rectangle, not one per line).</summary>
    private static void RenderRuns(DrawingContext context, TextView textView, SideBySideDiff diff, bool left,
        double x, double width, double scale)
    {
        if (ThemeBrushes.Get(left ? "DiffOverviewRemovedBrush" : "DiffOverviewInsertedBrush") is not { } brush)
            return;
        var lines = left ? diff.Left : diff.Right;
        var count = Math.Min(lines.Count, textView.Document?.LineCount ?? 0);
        int? start = null;
        for (var i = 0; i <= count; i++)
        {
            var changed = i < count && lines[i].Kind is DiffLineKind.Removed or DiffLineKind.Added or DiffLineKind.Modified;
            if (changed && start is null)
                start = i;
            else if (!changed && start is { } first)
            {
                context.FillRectangle(brush, LinesRect(textView, first + 1, i, x, width, scale));
                start = null;
            }
        }
    }
}
