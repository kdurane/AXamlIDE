using System;
using System.Threading.Tasks;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Editor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Locals window (Visual Studio's): the selected stack frame's variables, `this` included, as an expandable
/// tree. Re-read at every stop and frame change; values that changed since the last step in the same method are
/// marked; expansion is kept.
/// </summary>
public sealed partial class LocalsViewModel : ObservableObject
{
    private readonly DebugSnapshotTracker _debug;
    private string? _lastFrameName;
    private int _version;

    public LocalsViewModel(DebugSnapshotTracker debug, IMessenger messenger, IClipboardService clipboard)
    {
        _debug = debug;
        Tree = new DebugValueTree(messenger, clipboard);
        debug.Changed += () => _ = RefreshAsync();
        debug.ValuesEdited += () => _ = RefreshAsync();
    }

    public DebugValueTree Tree { get; }

    /// <summary>Shown instead of the tree when there's nothing to show.</summary>
    [ObservableProperty] public partial string? Message { get; set; } = "Locals are shown while the debugger is paused.";

    private async Task RefreshAsync()
    {
        var version = ++_version;
        if (_debug.Values is not { } values || _debug.SelectedFrame is not { } frame)
        {
            Tree.Clear();
            Message = "Locals are shown while the debugger is paused.";
            return;
        }

        try
        {
            var locals = await values.LocalsAsync();
            if (version != _version)
                return;
            var sameMethod = frame.Name == _lastFrameName;
            _lastFrameName = frame.Name;
            Message = locals.Count == 0 ? "No locals in this frame." : null;
            await Tree.SetRootsAsync(locals, values, markChanges: sameMethod);
        }
        catch (Exception ex) when (ex is DebugAdapterException or InvalidOperationException or TimeoutException)
        {
            if (version == _version)
                Message = $"Couldn't read the locals: {(ex is DebugAdapterException adapter ? adapter.AdapterMessage : ex.Message)}";
        }
    }
}
