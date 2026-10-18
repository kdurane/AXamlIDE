using AXamlIDE.Models;


namespace AXamlIDE.Messages;

/// <summary>
/// A document's diagnostics changed (new results from the server, or cleared).
/// Sent by LanguageSessionService; the Problems list and the editor pane react to it.
/// </summary>
public sealed record DiagnosticsChangedMessage(OpenDocument Document);
