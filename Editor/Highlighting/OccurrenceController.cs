using System;
using System.Collections.Generic;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// Highlights other occurrences of the selected text
/// </summary>
public sealed class OccurrenceController
{
    private readonly EditorContext _ctx;
    private readonly OccurrenceRenderer _renderer = new();

    /// <summary>Where the selected text occurs in the document (empty when nothing is selected).</summary>
    public IReadOnlyList<TextSegment> Segments => _renderer.Segments;

    /// <summary>The occurrences were recomputed.</summary>
    public event Action? Changed;

    public OccurrenceController(EditorContext ctx)
    {
        _ctx = ctx;

        ctx.Editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);

        ctx.Editor.TextArea.SelectionChanged += (_, _) => Update();
        ctx.TextChanged += _ => Update();
        ctx.Attached += _ => Update(); // also covers the "no tab open" reset
        ctx.FeaturesChanged += Update;
    }

    private void Update()
    {
        var editor = _ctx.Editor;
        var selectedText = editor.TextArea.Selection.IsEmpty || !_ctx.Settings.Features.HighlightSelectionOccurrences
            ? null
            : editor.SelectedText;

        _renderer.Update(_ctx.Active?.Document, selectedText);
        editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        Changed?.Invoke();
    }
}
