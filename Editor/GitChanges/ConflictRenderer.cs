using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Rendering;
using AXamlIDE.Services.Git;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.GitChanges;

/// <summary>
/// Merge conflicts git wrote into the file, coloured as VS Code does: the current side (ours) green, the incoming
/// side (theirs) blue, the marker lines stronger. Found again a moment after each edit, only in files that have
/// markers. Accept Current / Incoming / Both (the editor's right-click menu) replace the one at the caret.
/// </summary>
public sealed class ConflictRenderer : IBackgroundRenderer
{
    private readonly EditorContext _ctx;
    private IReadOnlyList<MergeConflict> _conflicts = [];

    public ConflictRenderer(EditorContext ctx)
    {
        _ctx = ctx;
        ctx.Attached += _ => Refresh();
        ctx.TextChanged += _ => Refresh();
    }

    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The conflict around a 1-based line, or null.</summary>
    public MergeConflict? ConflictAt(int line)
    {
        foreach (var conflict in _conflicts)
            if (conflict.Contains(line))
                return conflict;
        return null;
    }

    /// <summary>Accept Current / Incoming / Both Changes on the conflict at the caret (one undoable edit).</summary>
    public bool Resolve(ConflictChoice choice)
    {
        if (_ctx.Active is not { } document || ConflictAt(_ctx.Editor.TextArea.Caret.Line) is not { } conflict)
        {
            _ctx.Status("The caret isn't in a merge conflict.");
            return false;
        }
        var edit = MergeConflicts.Resolve(document.Document.Text, conflict, choice);
        document.Document.Replace(edit.Offset, edit.Length, edit.Text);
        return true;
    }

    private void Refresh()
    {
        var found = _ctx.Active is { } document ? MergeConflicts.Find(document.Document.Text) : [];
        if (found.Count == 0 && _conflicts.Count == 0)
            return;
        _conflicts = found;
        _ctx.Editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_conflicts.Count == 0 || !textView.VisualLinesValid)
            return;
        var current = Brush("GitGutterAddedBrush");
        var incoming = Brush("GitGutterModifiedBrush");
        foreach (var conflict in _conflicts)
        {
            Fill(textView, drawingContext, conflict.StartLine, conflict.StartLine, current, 0.45);
            Fill(textView, drawingContext, conflict.StartLine + 1, (conflict.BaseLine ?? conflict.SeparatorLine) - 1, current, 0.18);
            Fill(textView, drawingContext, conflict.SeparatorLine + 1, conflict.EndLine - 1, incoming, 0.18);
            Fill(textView, drawingContext, conflict.EndLine, conflict.EndLine, incoming, 0.45);
        }
    }

    private static Color? Brush(string key) => (ThemeBrushes.Get(key) as ISolidColorBrush)?.Color;

    private static void Fill(TextView textView, DrawingContext context, int first, int last, Color? color, double opacity)
    {
        if (color is not { } c || first > last)
            return;
        var brush = new ImmutableSolidColorBrush(c, opacity);
        foreach (var visualLine in textView.VisualLines)
        {
            var line = visualLine.FirstDocumentLine.LineNumber;
            if (line < first || line > last)
                continue;
            var top = visualLine.VisualTop - textView.VerticalOffset;
            context.FillRectangle(brush, new Rect(0, top, Math.Max(textView.Bounds.Width, 1), visualLine.Height));
        }
    }
}
