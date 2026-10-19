using AXamlIDE.Services.Debugging;

namespace AXamlIDE.Messages;

/// <summary>Open the visualizer for a value (a collection's table, a string's text). Handled by VisualizerManager.</summary>
public sealed record VisualizeDebugValueMessage(DebugValue Value, DebugValueProvider Provider);

/// <summary>Add an expression to the Watch window (DataTip / Locals / editor "Add Watch"). Handled by WatchViewModel.</summary>
public sealed record AddWatchMessage(string Expression);

/// <summary>A value was changed in a debugger view; the others re-read theirs.</summary>
public sealed record DebugValuesEditedMessage;
