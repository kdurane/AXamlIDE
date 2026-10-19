using System;

namespace AXamlIDE.Services.Debugging;

/// <summary>The debug adapter answered a request with an error ("error: The name 'x' does not exist...").</summary>
public sealed class DebugAdapterException(string command, string adapterMessage, string message)
    : InvalidOperationException(message)
{
    public string Command { get; } = command;

    /// <summary>The adapter's own message, without its "error: " prefix, for showing to the user.</summary>
    public string AdapterMessage { get; } = adapterMessage.StartsWith("error: ", StringComparison.Ordinal)
        ? adapterMessage["error: ".Length..]
        : adapterMessage;
}
