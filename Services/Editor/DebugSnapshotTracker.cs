using System;
using AXamlIDE.Messages;
using AXamlIDE.Services.Debugging;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Services.Editor;

/// <summary>
/// Remembers the debugger's latest stop (from <see cref="DebugSnapshotChangedMessage"/>), so an
/// editor opened after the stop (e.g. the file the debugger just navigated to) can still show the
/// inline values. Editors read <see cref="Current"/> when created and follow <see cref="Changed"/>.
/// </summary>
public sealed class DebugSnapshotTracker : IRecipient<DebugSnapshotChangedMessage>
{
    public DebugSnapshotTracker(IMessenger messenger) => messenger.RegisterAll(this);

    /// <summary>Null while the debuggee is running, or when there is no debug session.</summary>
    public StopSnapshot? Current { get; private set; }

    public event Action? Changed;

    public void Receive(DebugSnapshotChangedMessage message)
    {
        Current = message.Snapshot;
        Changed?.Invoke();
    }
}
