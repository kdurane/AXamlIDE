using System;
using AvaloniaEdit.Rendering;
using AXamlIDE.Lsp;

namespace AXamlIDE.EditorFeatures
{
    /// <summary>
    /// Highlights other occurrences of the selected text. Replaces MainWindow's
    /// _occurrenceRenderer, _occurrenceDocument, UpdateOccurrenceHighlights and
    /// OnOccurrenceDocumentChanged.
    ///
    /// Construct it exactly where the old
    ///   Editor.TextArea.TextView.BackgroundRenderers.Add(_occurrenceRenderer);
    /// line was, so the renderer order (diagnostics first, occurrences second) is unchanged.
    /// </summary>
    public sealed class OccurrenceController
    {
        private readonly EditorContext _ctx;
        private readonly OccurrenceRenderer _renderer = new();

        public OccurrenceController(EditorContext ctx)
        {
            _ctx = ctx;

            ctx.Editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);

            ctx.Editor.TextArea.SelectionChanged += (_, _) => Update();
            ctx.TextChanged += _ => Update();
            ctx.Attached += _ => Update(); // also covers the "no tab open" reset
        }

        public void Update()
        {
            var editor = _ctx.Editor;
            var selectedText = editor.TextArea.Selection.IsEmpty
                ? null
                : editor.SelectedText;

            _renderer.Update(_ctx.Active?.Document, selectedText);
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }
    }
}
