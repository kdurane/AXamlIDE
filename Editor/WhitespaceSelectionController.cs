using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace AXamlIDE.Editor;

/// <summary>
/// Double-clicking a run of spaces/tabs selects that run (as in Visual Studio and Rider). AvaloniaEdit on its own
/// selects the word to the left of the gap. Double-clicks on anything else are left to AvaloniaEdit.
/// </summary>
public sealed class WhitespaceSelectionController
{
    private readonly TextArea _area;

    public WhitespaceSelectionController(EditorContext ctx)
    {
        _area = ctx.Editor.TextArea;
        // Tunnel, so this runs before AvaloniaEdit's own selection handler.
        _area.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || e.KeyModifiers != KeyModifiers.None
            || !e.GetCurrentPoint(_area).Properties.IsLeftButtonPressed)
            return;
        if (WhitespaceAt(e.GetPosition(_area.TextView)) is not var (start, end))
            return;

        _area.Caret.Offset = end;
        _area.Selection = Selection.Create(_area, start, end);
        e.Handled = true;
    }

    /// <summary>The run of spaces/tabs under the pointer (the character it is over, not the nearest gap), or null.</summary>
    private (int Start, int End)? WhitespaceAt(Point pointInTextView)
    {
        var view = _area.TextView;
        var point = pointInTextView + view.ScrollOffset;
        if (view.GetVisualLineFromVisualTop(point.Y) is not { } visualLine || _area.Document is not { } document)
            return null;

        var offset = visualLine.FirstDocumentLine.Offset + visualLine.GetRelativeOffset(visualLine.GetVisualColumnFloor(point));
        var line = document.GetLineByOffset(offset);
        if (offset >= line.EndOffset || !IsBlank(document, offset))
            return null;   // past the end of the line, or not on whitespace

        var start = offset;
        while (start > line.Offset && IsBlank(document, start - 1))
            start--;
        var end = offset + 1;
        while (end < line.EndOffset && IsBlank(document, end))
            end++;
        return (start, end);
    }

    private static bool IsBlank(TextDocument document, int offset) => document.GetCharAt(offset) is ' ' or '\t';
}
