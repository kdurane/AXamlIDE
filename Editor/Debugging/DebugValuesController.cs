using System;
using AvaloniaEdit.Rendering;
using AXamlIDE.Services.Editor;

namespace AXamlIDE.Editor.Debugging;

/// <summary>
/// While paused, in this editor: the current statement (line highlight + the margin's arrow; green for another
/// frame picked in the Call Stack) and the inline "name = value" hints near the top frame's line.
/// </summary>
public sealed class DebugValuesController : IDisposable
{
    private readonly EditorContext _ctx;
    private readonly DebugSnapshotTracker _snapshots;
    private readonly BreakpointMargin _margin;
    private readonly DebugInlineValueGenerator _inlineValues = new();
    private readonly CurrentStatementRenderer _statement = new();

    public DebugValuesController(EditorContext ctx, DebugSnapshotTracker snapshots, BreakpointMargin margin)
    {
        _ctx = ctx;
        _snapshots = snapshots;
        _margin = margin;
        var textView = ctx.Editor.TextArea.TextView;
        textView.ElementGenerators.Add(_inlineValues);
        textView.BackgroundRenderers.Add(_statement);

        _snapshots.Changed += Apply;
        _snapshots.InlineValuesChanged += Apply;
        _ctx.Attached += _ => Apply();
        _ctx.FeaturesChanged += Apply;
    }

    public void Dispose()
    {
        _snapshots.Changed -= Apply;
        _snapshots.InlineValuesChanged -= Apply;
    }

    private void Apply()
    {
        var stop = _snapshots.Current;
        var path = _ctx.Active?.Path;

        if (stop?.Top is { Path: { } topPath } top && string.Equals(topPath, path, StringComparison.Ordinal)
            && _ctx.Settings.Features.InlineDebugValues)
            _inlineValues.Show(_snapshots.InlineValues, top.Line);
        else
            _inlineValues.Clear();

        (int, bool)? statement = _snapshots.SelectedFrame is { Path: { } framePath } frame && string.Equals(framePath, path, StringComparison.Ordinal)
            ? (frame.Line, frame == stop?.Top)
            : null;
        _statement.Statement = statement;
        _margin.CurrentStatement = statement;

        var textView = _ctx.Editor.TextArea.TextView;
        textView.Redraw();
        textView.InvalidateLayer(KnownLayer.Background);
    }
}
