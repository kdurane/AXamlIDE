using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AXamlIDE.Models;

namespace AXamlIDE.Editor;

/// <summary>
/// Keeps the text on screen still when an edit lands wholly above it, such as a quick fix adding a using at the
/// top of the file. AvaloniaEdit keeps the pixel scroll offset, so on its own the view would slide down by the
/// lines inserted. Visual Studio and VS Code instead keep the top visible line where it was.
/// Only for edits made through <see cref="OpenDocument.EditKeepingView"/> (code actions): undo, redo and the
/// like must still scroll to what they changed, which keeping the view still would fight (and flicker).
/// </summary>
public sealed class ScrollAnchorController
{
    private readonly EditorContext _ctx;
    private OpenDocument? _open;
    private TextDocument? _hooked;

    // The top visible line (as an anchor, so it follows the edits) and how far into it the view was scrolled.
    private TextAnchor? _anchor;
    private double _intoLine;

    public ScrollAnchorController(EditorContext ctx)
    {
        _ctx = ctx;
        ctx.Detaching += _ => Unhook();
        ctx.Attached += doc =>
        {
            _open = doc;
            _hooked = doc?.Document;
            if (_hooked is null)
                return;
            _hooked.Changing += OnChanging;
            _hooked.Changed += OnChanged;
        };
    }

    private void Unhook()
    {
        if (_hooked is not null)
        {
            _hooked.Changing -= OnChanging;
            _hooked.Changed -= OnChanged;
        }
        _hooked = null;
        _open = null;
        _anchor = null;
    }

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_anchor is not null || _hooked is not { } document || _open is not { KeepViewStill: true })
            return;   // already anchored for this run of edits, or not a code action's edit

        var view = _ctx.Editor.TextArea.TextView;
        var scrollY = view.ScrollOffset.Y;
        if (scrollY <= 0 || view.GetDocumentLineByVisualTop(scrollY) is not { } topLine)
            return;
        if (e.Offset + e.RemovalLength >= topLine.Offset)
            return;   // touches the visible text (or below it): typing and the like scroll as usual

        _anchor = document.CreateAnchor(topLine.Offset);
        _anchor.MovementType = AnchorMovementType.AfterInsertion;
        _intoLine = scrollY - view.GetVisualTopByDocumentLine(topLine.LineNumber);
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_anchor is null)
            return;
        Restore();
        // Once laid out again, in case the scroll viewer clamped the offset to its old extent.
        Dispatcher.UIThread.Post(() =>
        {
            Restore();
            _anchor = null;
        }, DispatcherPriority.Loaded);
    }

    private void Restore()
    {
        if (_anchor is not { IsDeleted: false } anchor)
            return;
        var editor = _ctx.Editor;
        var view = editor.TextArea.TextView;
        var top = view.GetVisualTopByDocumentLine(anchor.Line) + _intoLine;
        if (System.Math.Abs(view.ScrollOffset.Y - top) > 0.5)
            editor.ScrollToOffset(view.ScrollOffset.X, top);
    }
}
