using AvaloniaEdit.TextMate;
using AXamlIDE.Debugging;
using AXamlIDE.Documents;
using AXamlIDE.Lsp;
using AXamlIDE.Outline;
using AXamlIDE.Problems;
using AXamlIDE.Search;

namespace AXamlIDE.Editor;

// Messages the editor pane exchanges with the rest of the app (via IMessenger).
// To find who handles one, search for Register<ThatMessage> or IRecipient<ThatMessage>.

// ---- Into the editor: "go to this place".

public sealed record NavigateToProblemMessage(ProblemItem Problem);
public sealed record NavigateToDebugLocationMessage(string Path, int Line);
public sealed record NavigateToSearchResultMessage(WorkspaceSearchResult Result, string SearchQuery);
public sealed record NavigateToOutlineItemMessage(DocumentOutlineItem Item);
public sealed record NavigateToLocationMessage(LspLocation Location);

// ---- Into the editor: state changes it reflects.

/// <summary>A null snapshot means the debuggee is running again or has ended.</summary>
public sealed record DebugSnapshotChangedMessage(StopSnapshot? Snapshot);
public sealed record DiagnosticsChangedMessage(OpenDocument Document);

// ---- Out of the editor.

public sealed record StatusMessage(string Text);
/// <summary>Caret moved or diagnostics changed; whoever owns the status bar refreshes it.</summary>
public sealed record EditorStateChangedMessage;
public sealed record EditorThemeAppliedMessage(TextMate.Installation Installation);
