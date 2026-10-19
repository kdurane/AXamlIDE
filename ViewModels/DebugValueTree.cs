using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The rows of a debugger value view (Views/DebugValueTreeView), as Visual Studio's Locals / Watch / DataTips show
/// them: a flat list (so it virtualizes and the columns line up) where expanding a row inserts its children
/// beneath it, indented. Expansion is remembered by path across steps, and values that changed since the last stop
/// are marked (red in the view).
/// </summary>
public sealed partial class DebugValueTree : ObservableObject
{
    private readonly IClipboardService? _clipboard;
    private readonly HashSet<string> _expandedPaths = new(StringComparer.Ordinal);
    private Dictionary<string, string> _previousDisplays = new(StringComparer.Ordinal);
    private int _generation;

    public DebugValueTree(IMessenger messenger, IClipboardService? clipboard = null)
    {
        Messenger = messenger;
        _clipboard = clipboard;
    }

    public IMessenger Messenger { get; }

    public ObservableCollection<DebugValueNode> Rows { get; } = [];

    /// <summary>Reads the values; null while the debuggee runs (the rows are cleared then).</summary>
    public DebugValueProvider? Provider { get; private set; }

    /// <summary>
    /// Show these values (a new stop, frame or watch list), keeping what was expanded. Without a provider (the
    /// debuggee is running) the rows are shown but can't be expanded.
    /// </summary>
    public async Task SetRootsAsync(IReadOnlyList<DebugValue> roots, DebugValueProvider? provider, bool markChanges = true)
    {
        var generation = ++_generation;
        Provider = provider;
        var previous = markChanges ? _previousDisplays : new Dictionary<string, string>(StringComparer.Ordinal);
        var displays = new Dictionary<string, string>(StringComparer.Ordinal);

        Rows.Clear();
        foreach (var root in roots)
            Rows.Add(NewNode(root, 0, root.Name, previous, displays));

        // Re-open what was open, top to bottom (children of re-opened rows included).
        for (var i = 0; i < Rows.Count && provider is not null; i++)
        {
            var node = Rows[i];
            if (node.HasChildren && _expandedPaths.Contains(node.Path))
            {
                await ExpandAsync(node, previous, displays);
                if (generation != _generation)
                    return;   // replaced while reading
            }
        }
        _previousDisplays = displays;
    }

    /// <summary>The debuggee runs again: nothing to show (VS greys the views; ours empty).</summary>
    public void Clear()
    {
        _generation++;
        Provider = null;
        Rows.Clear();
    }

    /// <summary>Replace one root (a watch edited or re-evaluated) in place.</summary>
    public async Task ReplaceRootAsync(int rootIndex, DebugValue value)
    {
        if (Provider is null)
            return;
        var roots = Rows.Where(row => row.Level == 0).ToList();
        if (rootIndex < 0 || rootIndex >= roots.Count)
            return;
        var start = Rows.IndexOf(roots[rootIndex]);
        Collapse(roots[rootIndex]);
        var node = NewNode(value, 0, value.Name, _previousDisplays, null);
        Rows[start] = node;
        if (node.HasChildren && _expandedPaths.Contains(node.Path))
            await ExpandAsync(node, _previousDisplays, null);
    }

    private DebugValueNode NewNode(DebugValue value, int level, string path, IReadOnlyDictionary<string, string> previous,
        Dictionary<string, string>? displays)
    {
        var node = new DebugValueNode(this, value, level, path)
        {
            IsChanged = previous.TryGetValue(path, out var before) && before != value.Display
        };
        displays?.TryAdd(path, value.Display);
        return node;
    }

    // ---------------------------------------------------------------- expand / collapse

    public async Task ToggleAsync(DebugValueNode node)
    {
        if (!node.HasChildren || node.IsLoading)
            return;
        if (node.IsExpanded)
        {
            _expandedPaths.Remove(node.Path);
            Collapse(node);
        }
        else
        {
            _expandedPaths.Add(node.Path);
            await ExpandAsync(node, _previousDisplays, null);
        }
    }

    private async Task ExpandAsync(DebugValueNode node, IReadOnlyDictionary<string, string> previous, Dictionary<string, string>? displays)
    {
        if (Provider is not { } provider)
            return;
        var generation = _generation;
        if (node.Children is null)
        {
            node.IsLoading = true;
            try
            {
                var values = await provider.ChildrenAsync(node.Value);
                if (generation != _generation)
                    return;
                node.Children = values.Select(value => NewNode(value, node.Level + 1, node.Path + "/" + value.Name, previous, displays)).ToList();
            }
            catch (Exception ex) when (ex is DebugAdapterException or InvalidOperationException or TimeoutException)
            {
                node.Children = [NewNode(DebugValueProvider.Error("", ex is DebugAdapterException adapter ? adapter.AdapterMessage : ex.Message), node.Level + 1, node.Path + "/!", previous, null)];
            }
            finally
            {
                node.IsLoading = false;
            }
        }

        var index = Rows.IndexOf(node);
        if (index < 0)
            return;
        node.IsExpanded = true;
        var at = index + 1;
        foreach (var child in node.Children)
            Rows.Insert(at++, child);

        // Children that were open stay open.
        foreach (var child in node.Children.ToList())
            if (child.HasChildren && _expandedPaths.Contains(child.Path) && !child.IsExpanded)
                await ExpandAsync(child, previous, displays);
    }

    private void Collapse(DebugValueNode node)
    {
        if (!node.IsExpanded)
            return;
        node.IsExpanded = false;
        var index = Rows.IndexOf(node);
        while (index + 1 < Rows.Count && Rows[index + 1].Level > node.Level)
        {
            var child = Rows[index + 1];
            child.IsExpanded = false;   // shown collapsed if re-inserted; re-expanded from _expandedPaths
            Rows.RemoveAt(index + 1);
        }
    }

    // ---------------------------------------------------------------- row actions

    public void Visualize(DebugValueNode node)
    {
        if (node.CanVisualize && Provider is { } provider)
            Messenger.Send(new VisualizeDebugValueMessage(node.Value, provider));
    }

    public async Task CopyAsync(string text)
    {
        if (_clipboard is not null)
            await _clipboard.SetTextAsync(text);
    }

    public async Task CommitEditAsync(DebugValueNode node)
    {
        node.IsEditing = false;
        if (Provider is not { } provider || node.EditText == node.Display)
            return;
        var updated = await provider.SetValueAsync(node.Value, node.EditText);
        node.Value = updated with { Name = node.Name };
        Messenger.Send(new DebugValuesEditedMessage());
    }
}
