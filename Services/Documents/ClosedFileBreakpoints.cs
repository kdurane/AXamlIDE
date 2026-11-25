using System;
using System.Collections.Generic;
using System.Linq;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Documents;

/// <summary>
/// The breakpoints of files that aren't open (open files keep theirs on the <see cref="OpenDocument"/>). Closing a
/// file moves its breakpoints here and opening it moves them back, so a breakpoint outlives its tab as in Visual
/// Studio, and the debugger still stops at it. Only while a workspace is open: WorkspaceSession starts it with
/// the workspace's remembered breakpoints (<see cref="Begin"/>) and stops it when the workspace closes (<see cref="End"/>).
/// </summary>
public sealed class ClosedFileBreakpoints
{
    private readonly Dictionary<string, List<BreakpointInfo>> _byPath = new(StringComparer.Ordinal);
    private bool _tracking;

    public ClosedFileBreakpoints(DocumentManager documents)
    {
        documents.Opened += OnOpened;
        documents.Closing += OnClosing;
    }

    /// <summary>Breakpoints here changed for this file (it was closed, opened, or one was switched or deleted from the list).</summary>
    public event Action<string>? Changed;

    /// <summary>Every file with breakpoints here, and its breakpoints in line order.</summary>
    public IEnumerable<(string Path, IReadOnlyList<BreakpointInfo> Breakpoints)> All
        => _byPath.Select(pair => (pair.Key, (IReadOnlyList<BreakpointInfo>)pair.Value));

    /// <summary>This file's breakpoints (empty when it has none here, or it's open).</summary>
    public IReadOnlyList<BreakpointInfo> For(string path) => _byPath.TryGetValue(path, out var list) ? list : [];

    /// <summary>A workspace opened: hold its remembered breakpoints of files that aren't open.</summary>
    public void Begin(IEnumerable<(string Path, BreakpointInfo Breakpoint)> breakpoints)
    {
        _byPath.Clear();
        foreach (var group in breakpoints.GroupBy(item => item.Path, StringComparer.Ordinal))
            _byPath[group.Key] = [.. group.Select(item => item.Breakpoint).DistinctBy(b => b.Line).OrderBy(b => b.Line)];
        _tracking = true;
    }

    /// <summary>The workspace closed: forget its breakpoints (its session has saved them).</summary>
    public void End()
    {
        _tracking = false;
        var paths = _byPath.Keys.ToList();
        _byPath.Clear();
        foreach (var path in paths)
            Changed?.Invoke(path);
    }

    /// <summary>Turn one on or off (the Breakpoints list's check box).</summary>
    public void SetEnabled(string path, int line, bool enabled)
        => Replace(path, list => list.Select(b => b.Line == line ? b with { IsEnabled = enabled } : b));

    public void Remove(string path, int line) => Replace(path, list => list.Where(b => b.Line != line));

    /// <summary>Debug > Delete All Breakpoints.</summary>
    public void Clear()
    {
        var paths = _byPath.Keys.ToList();
        _byPath.Clear();
        foreach (var path in paths)
            Changed?.Invoke(path);
    }

    /// <summary>Debug > Enable / Disable All Breakpoints.</summary>
    public void SetAllEnabled(bool enabled)
    {
        foreach (var path in _byPath.Keys.ToList())
            Replace(path, list => list.Select(b => b with { IsEnabled = enabled }));
    }

    private void Replace(string path, Func<IEnumerable<BreakpointInfo>, IEnumerable<BreakpointInfo>> change)
    {
        if (!_byPath.TryGetValue(path, out var list))
            return;
        var changed = change(list).ToList();
        if (changed.SequenceEqual(list))
            return;
        if (changed.Count == 0)
            _byPath.Remove(path);
        else
            _byPath[path] = changed;
        Changed?.Invoke(path);
    }

    private void OnOpened(OpenDocument document)
    {
        if (!_byPath.Remove(document.Path, out var breakpoints))
            return;
        document.RestoreBreakpoints(breakpoints);
        Changed?.Invoke(document.Path);
    }

    private void OnClosing(OpenDocument document)
    {
        if (!_tracking || document.IsReadOnly || document.Breakpoints is not { Count: > 0 } breakpoints)
            return;
        _byPath[document.Path] = [.. breakpoints];
        Changed?.Invoke(document.Path);
    }
}
