using System;
using AXamlIDE.Services.Editor;

namespace AXamlIDE.Editor.Debugging;

/// <summary>
/// Shows the debugger's inline values in this editor while paused, when the stop is in its document.
/// </summary>
public sealed class DebugValuesController : IDisposable
{
    private readonly EditorContext _ctx;
    private readonly DebugSnapshotTracker _snapshots;
    private readonly DebugInlineValueGenerator _inlineValues = new();

    public DebugValuesController(EditorContext ctx, DebugSnapshotTracker snapshots)
    {
        _ctx = ctx;
        _snapshots = snapshots;
        ctx.Editor.TextArea.TextView.ElementGenerators.Add(_inlineValues);

        _snapshots.Changed += Apply;
        _ctx.Attached += _ => Apply();
    }

    public void Dispose() => _snapshots.Changed -= Apply;

    private void Apply()
    {
        if (_snapshots.Current?.Top is { Path: { } path } top
            && _ctx.Active is { } active
            && string.Equals(path, active.Path, StringComparison.Ordinal))
            _inlineValues.Show(_snapshots.Current.Values, top.Line);
        else
            _inlineValues.Clear();

        _ctx.Editor.TextArea.TextView.Redraw();
    }
}
