using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace AXamlIDE.Editor;

/// <summary>
/// AvaloniaEdit's <see cref="TextEditor"/> with a fix for a layout loop in AvaloniaEdit 12.0.0 (still in its master branch,
/// and likely AvaloniaEdit issue #570). Dragging a selection (or a rectangle selection) past the right edge on the widest
/// line in view, or past the bottom with AllowScrollBelowDocument off, froze the editor with "Layout cycle detected"
/// for TextView. The selection drag scrolls the caret into view with a 5-pixel border, which asks for a scroll position
/// past the end of what can be scrolled. The arrange pass pulls it back, which raises ScrollOffsetChanged, which makes
/// the selection drag scroll out again, and so on forever. Here the text view ignores scroll requests while it arranges.
/// (Capping the request at the scrollable area wasn't enough: with a fractional editor width, as a dock pane split by
/// proportion gives, the measured and arranged widths differ by a fraction of a pixel, and the two still fought.)
/// </summary>
public class IdeTextEditor() : TextEditor(new IdeTextArea())
{
    protected override Type StyleKeyOverride => typeof(TextEditor);

    private sealed class IdeTextArea : TextArea
    {
        public IdeTextArea() : base(new ClampedTextView())
        {
            // What TextArea's public constructor adds (its protected one, needed to pass our TextView, doesn't).
            AddHandler(KeyDownEvent, (_, e) => ForEachInputHandler(e, (h, args) => h.OnPreviewKeyDown(args)), RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, (_, e) => ForEachInputHandler(e, (h, args) => h.OnPreviewKeyUp(args)), RoutingStrategies.Tunnel);
        }

        protected override Type StyleKeyOverride => typeof(TextArea);

        private void ForEachInputHandler(KeyEventArgs e, Action<TextAreaStackedInputHandler, KeyEventArgs> handle)
        {
            foreach (var handler in StackedInputHandlers)
            {
                if (e.Handled)
                    break;
                handle(handler, e);
            }
        }
    }

    private sealed class ClampedTextView : TextView
    {
        private bool _arranging;

        protected override Type StyleKeyOverride => typeof(TextView);

        protected override Size ArrangeOverride(Size finalSize)
        {
            _arranging = true;
            try
            {
                return base.ArrangeOverride(finalSize);
            }
            finally
            {
                _arranging = false;
            }
        }

        public override void MakeVisible(Rect rectangle)
        {
            // While arranging, the text view pulls a scroll position past the end back into range. The only thing that
            // asks to scroll then is the selection drag, reacting to that very change, so let the correction stand.
            if (!_arranging)
                base.MakeVisible(rectangle);
        }
    }
}
