using System;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Snippets;
using AXamlIDE.Views;

namespace AXamlIDE.Editor.Snippets;

/// <summary>
/// A snippet field (a tab stop the user types over). AvaloniaEdit's own SnippetReplaceableTextElement draws its fields
/// lime green with a black border whatever the theme; this one is the same element drawn with the theme's word
/// highlight, outlined while the caret is in it. Fields that repeat it (SnippetBoundElement) follow its text.
/// </summary>
public sealed class SnippetFieldElement : SnippetReplaceableTextElement
{
    public override void Insert(InsertionContext context)
    {
        var start = context.InsertionPosition;
        context.InsertText(Text);
        context.RegisterActiveElement(this, new ActiveField(context, start, context.InsertionPosition));
    }

    private sealed class ActiveField(InsertionContext context, int startOffset, int endOffset) : IReplaceableActiveElement
    {
        private TextAnchor? _start;
        private TextAnchor? _end;
        private Renderer? _background;
        private Renderer? _outline;
        private bool _caretInside;

        public string Text { get; private set; } = "";
        public event EventHandler? TextChanged;
        public bool IsEditable => true;

        public ISegment? Segment => _start is null || _end is null || _start.IsDeleted || _end.IsDeleted
            ? null
            : new SimpleSegment(_start.Offset, Math.Max(0, _end.Offset - _start.Offset));

        public void OnInsertionCompleted()
        {
            // Anchors only now, so they move with what the user types, not with the rest of the snippet going in.
            _start = context.Document.CreateAnchor(startOffset);
            _start.MovementType = AnchorMovementType.BeforeInsertion;
            _end = context.Document.CreateAnchor(endOffset);
            _end.MovementType = AnchorMovementType.AfterInsertion;
            _start.Deleted += OnAnchorDeleted;
            _end.Deleted += OnAnchorDeleted;
            context.Document.TextChanged += OnTextChanged;

            var textView = context.TextArea.TextView;
            _background = new Renderer(this, KnownLayer.Background);
            _outline = new Renderer(this, KnownLayer.Text);
            textView.BackgroundRenderers.Add(_background);
            textView.BackgroundRenderers.Add(_outline);
            context.TextArea.Caret.PositionChanged += OnCaretMoved;
            OnCaretMoved(null, EventArgs.Empty);
            Text = CurrentText();
        }

        public void Deactivate(SnippetEventArgs e)
        {
            context.Document.TextChanged -= OnTextChanged;
            context.TextArea.TextView.BackgroundRenderers.Remove(_background);
            context.TextArea.TextView.BackgroundRenderers.Remove(_outline);
            context.TextArea.Caret.PositionChanged -= OnCaretMoved;
        }

        private void OnAnchorDeleted(object? sender, EventArgs e) => context.Deactivate(new SnippetEventArgs(DeactivateReason.Deleted));

        private void OnTextChanged(object? sender, EventArgs e)
        {
            var text = CurrentText();
            if (text == Text)
                return;
            Text = text;
            TextChanged?.Invoke(this, e);
        }

        private void OnCaretMoved(object? sender, EventArgs e)
        {
            if (Segment is not { } segment)
                return;
            var inside = segment.Offset <= context.TextArea.Caret.Offset && context.TextArea.Caret.Offset <= segment.EndOffset;
            if (inside == _caretInside)
                return;
            _caretInside = inside;
            context.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
        }

        private string CurrentText() => Segment is { } segment ? context.Document.GetText(segment) : "";

        private sealed class Renderer(ActiveField field, KnownLayer layer) : IBackgroundRenderer
        {
            public KnownLayer Layer => layer;

            public void Draw(TextView textView, DrawingContext drawingContext)
            {
                if (field.Segment is not { } segment || layer == KnownLayer.Text && !field._caretInside)
                    return;
                var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, BorderThickness = 1 };
                builder.AddSegment(textView, segment);
                if (builder.CreateGeometry() is not { } geometry)
                    return;
                if (layer == KnownLayer.Background)
                    drawingContext.DrawGeometry(ThemeBrushes.Get("WordHighlightBrush"), null, geometry);
                else if (ThemeBrushes.Get("ThemeForegroundBrush") is { } outline)
                    drawingContext.DrawGeometry(null, new Pen(outline, 1, DashStyle.Dot), geometry);
            }
        }
    }
}
