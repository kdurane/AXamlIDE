using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Editor.GitChanges;

/// <summary>
/// What a changed block was in git, in a popup under it (EditorPaneView.axaml's GitChangePopup): VS Code's quick
/// diff peek and Rider's change popup. Opens when a change bar is clicked, or by Next / Previous Change; Revert
/// puts the block back (an ordinary edit, so Ctrl+Z undoes it). Data: <see cref="GitChangeController"/>.
/// </summary>
public sealed class GitChangePeek
{
    private readonly EditorContext _ctx;
    private readonly GitChangeController _changes;
    private readonly Popup _popup;
    private readonly TextBlock _title;
    private readonly SelectableTextBlock _oldText;
    private readonly Border _oldBorder;
    private LineChange? _shown;

    public GitChangePeek(EditorContext ctx, GitChangeController changes, GitChangeMargin margin, Popup popup, TextBlock title,
        SelectableTextBlock oldText, Border oldBorder, Button previous, Button next, Button revert)
    {
        _ctx = ctx;
        _changes = changes;
        _popup = popup;
        _title = title;
        _oldText = oldText;
        _oldBorder = oldBorder;
        popup.PlacementTarget = ctx.Editor.TextArea.TextView;

        margin.ChangeClicked += Show;
        previous.Click += (_, _) => Move(next: false);
        next.Click += (_, _) => Move(next: true);
        revert.Click += (_, _) =>
        {
            if (_shown is { } change)
                Revert(change);
        };
        // The block moved or went (typing, a save, a commit): the popup would show something out of date.
        changes.Changed += () =>
        {
            if (_popup.IsOpen && _shown is { } change && !_changes.Changes.Contains(change))
                _popup.IsOpen = false;
        };
        ctx.Attached += _ => _popup.IsOpen = false;
    }

    private int CaretLine => _ctx.Editor.TextArea.Caret.Line;

    /// <summary>Next / Previous Change (VS Code's Alt+F5 / Shift+Alt+F5): move the caret to it and show it.</summary>
    public void Move(bool next)
    {
        var from = _popup.IsOpen && _shown is { } shown ? GitChangeController.Line(shown) : CaretLine;
        if (_changes.Neighbour(from, next) is not { } change)
        {
            _ctx.Status("No changes since the last commit in this file.");
            return;
        }
        var line = GitChangeController.Line(change);
        _ctx.Editor.TextArea.Caret.Line = line;
        _ctx.Editor.TextArea.Caret.Column = 1;
        _ctx.Editor.ScrollToLine(line);
        // After the scroll has laid out the line, so the popup is placed under it.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Show(change), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Revert Change: the block at the caret back as it is in git.</summary>
    public void RevertAtCaret()
    {
        if (_changes.ChangeAt(CaretLine) is { } change)
            Revert(change);
        else
            _ctx.Status("The caret isn't on a changed line.");
    }

    private void Revert(LineChange change)
    {
        _popup.IsOpen = false;
        if (!_changes.Revert(change))
            _ctx.Status("That change is out of date; try again.");
    }

    private void Show(LineChange change)
    {
        _shown = change;
        var old = _changes.BaseLines(change);
        _title.Text = change.Kind switch
        {
            LineChangeKind.Added => change.LineCount == 1 ? "1 line added" : $"{change.LineCount} lines added",
            LineChangeKind.Deleted => old.Count == 1 ? "1 line deleted" : $"{old.Count} lines deleted",
            _ => $"{change.LineCount} line{(change.LineCount == 1 ? "" : "s")} changed, from:"
        };
        _oldBorder.IsVisible = old.Count > 0;
        _oldText.Text = string.Join("\n", old.Select(line => line.TrimEnd('\r')));

        // Just below the block (a deletion: below the line it's under), lined up with the code's left edge.
        var textView = _ctx.Editor.TextArea.TextView;
        var below = change.Kind == LineChangeKind.Deleted ? Math.Max(1, change.FirstLine) : change.LastLine;
        if (textView.GetVisualLine(Math.Min(below, textView.Document?.LineCount ?? below)) is not { } visualLine)
            return;
        var y = visualLine.VisualTop + visualLine.Height - textView.VerticalOffset;
        _popup.PlacementRect = new Rect(0, y, 1, 1);
        _popup.IsOpen = true;
    }
}
