using AXamlIDE.Models;


namespace AXamlIDE.Messages;

/// <summary>
/// A document's diagnostics changed (new results from the server, or cleared).
/// Sent by LanguageSessionService; the Problems list and the editor pane react to it.
/// </summary>
public sealed record DiagnosticsChangedMessage(OpenDocument Document);

/// <summary>
/// Solution-wide analysis has new diagnostics for files that aren't open, or was switched on or off.
/// Sent by SolutionAnalysisService; the Problems list and the status bar read its <c>Files</c> again.
/// </summary>
public sealed record SolutionDiagnosticsChangedMessage;
