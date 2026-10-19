using System.Collections.Generic;
using AXamlIDE.Messages;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Editor;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Opens debugger visualizers (<see cref="VisualizeDebugValueMessage"/>, from the magnifier in DataTips, Locals and
/// Watch) as tabs: one per expression, read again when asked again. Marks them stale when the debuggee moves on.
/// </summary>
public sealed class VisualizerManager : IRecipient<VisualizeDebugValueMessage>
{
    private readonly DockLayoutViewModel _docking;
    private readonly IClipboardService _clipboard;
    private readonly Dictionary<string, VisualizerDockable> _open = [];

    public VisualizerManager(DockLayoutViewModel docking, DebugSnapshotTracker debug, IClipboardService clipboard, IMessenger messenger)
    {
        _docking = docking;
        _clipboard = clipboard;
        messenger.RegisterAll(this);
        debug.Changed += () =>
        {
            foreach (var page in _open.Values)
                page.IsStale = true;
        };
    }

    public void Receive(VisualizeDebugValueMessage message)
    {
        var expression = message.Value.Expression ?? message.Value.Name;
        var id = VisualizerDockable.IdFor(expression);
        if (!_open.TryGetValue(id, out var page) || !_docking.ShowPage(id))
        {
            page = new VisualizerDockable(expression, _clipboard);
            _open[id] = page;
            _docking.OpenPage(page);
        }
        _ = page.LoadAsync(message.Value, message.Provider);
    }
}
