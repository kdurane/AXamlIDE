using System.Collections.Generic;

namespace AXamlIDE.Messages;

// App-wide messages (sent through IMessenger). The shell (MainWindowViewModel) handles both.

/// <summary>Show this text in the status bar.</summary>
public sealed record StatusMessage(string Text);

/// <summary>Bring a tool panel to the front (a <see cref="ToolTab"/> id).</summary>
public sealed record ShowToolTabMessage(string PanelId);
/// <summary>Files were created outside the explorer (e.g. by a code action); the explorer shows them.</summary>
public sealed record FilesCreatedMessage(IReadOnlyList<string> Paths);
