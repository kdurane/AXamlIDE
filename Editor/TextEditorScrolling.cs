using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace AXamlIDE.Editor;

/// <summary>
/// Scrolling a <see cref="TextEditor"/> to an offset. AvaloniaEdit 12's own ScrollToVerticalOffset and
/// ScrollToHorizontalOffset are empty (they only apply the template), and its ScrollViewer is internal,
/// so this sets the offset on the editor's ScrollViewer found in its template.
/// </summary>
public static class TextEditorScrolling
{
    public static void ScrollToOffset(this TextEditor editor, double horizontal, double vertical)
    {
        editor.ApplyTemplate();
        if (editor.FindDescendantOfType<ScrollViewer>() is { } scrollViewer)
            scrollViewer.Offset = new Vector(horizontal, vertical);
    }
}
