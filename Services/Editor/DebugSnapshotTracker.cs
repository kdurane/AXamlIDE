using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Services.Debugging;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Editor;

/// <summary>
/// The debugger's latest stop (from <see cref="DebugSnapshotChangedMessage"/>) and the stack frame being looked at
/// (the top one, or the one picked in the Call Stack). Editors (inline values, DataTips) and the Locals / Watch /
/// Call Stack panels read <see cref="Current"/> and <see cref="Values"/>, and follow <see cref="Changed"/>.
/// </summary>
public sealed class DebugSnapshotTracker : IRecipient<DebugSnapshotChangedMessage>, IRecipient<DebugValuesEditedMessage>
{
    public DebugSnapshotTracker(IMessenger messenger) => messenger.RegisterAll(this);

    /// <summary>Null while the debuggee is running, or when there is no debug session.</summary>
    public StopSnapshot? Current { get; private set; }

    /// <summary>The frame whose locals are shown: the first with source, unless another was picked.</summary>
    public DebugFrame? SelectedFrame { get; private set; }

    /// <summary>Reads values in <see cref="SelectedFrame"/>; null while running.</summary>
    public DebugValueProvider? Values { get; private set; }

    /// <summary>A new stop, running again, or another frame picked.</summary>
    public event Action? Changed;

    /// <summary>A value was changed from a debugger view: values elsewhere may have changed too.</summary>
    public event Action? ValuesEdited;

    /// <summary>The top frame's simple locals and `this` members for the editor's inline "name = value" hints.</summary>
    public IReadOnlyDictionary<string, string> InlineValues { get; private set; } = new Dictionary<string, string>();

    /// <summary><see cref="InlineValues"/> were re-read after an edit.</summary>
    public event Action? InlineValuesChanged;

    public void Receive(DebugSnapshotChangedMessage message)
    {
        Current = message.Snapshot;
        InlineValues = Current?.Values ?? new Dictionary<string, string>();
        Select(Current?.Top ?? Current?.Frames.FirstOrDefault());
    }

    public void Receive(DebugValuesEditedMessage message)
    {
        ValuesEdited?.Invoke();
        _ = RefreshInlineValuesAsync();
    }

    /// <summary>After an edit: the top frame's simple locals again (the stop's own list is what they were).</summary>
    private async Task RefreshInlineValuesAsync()
    {
        if (Current is not { Top: { } top } stop)
            return;
        try
        {
            var values = new DebugValueProvider(stop.Debugger, top.Id, stop.Attributes);
            var locals = await values.LocalsAsync();
            if (!ReferenceEquals(stop, Current))
                return;
            var inline = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var local in locals)
                if (!local.HasChildren && local.Name != "this")
                    inline.TryAdd(local.Name, local.Display.Length <= 40 ? local.Display : local.Display[..39] + "…");
            foreach (var (name, value) in stop.Values)
                inline.TryAdd(name, value);   // `this` members, as at the stop
            InlineValues = inline;
            InlineValuesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is DebugAdapterException or InvalidOperationException or TimeoutException)
        {
        }
    }

    /// <summary>Look at another frame of the current stop (the Call Stack's double-click).</summary>
    public void SelectFrame(DebugFrame frame)
    {
        if (Current is not null && Current.Frames.Contains(frame) && frame != SelectedFrame)
            Select(frame);
    }

    private void Select(DebugFrame? frame)
    {
        SelectedFrame = frame;
        Values = Current is { } stop && frame is not null ? new DebugValueProvider(stop.Debugger, frame.Id, stop.Attributes) : null;
        Changed?.Invoke();
    }
}
