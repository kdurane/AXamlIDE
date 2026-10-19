namespace AXamlIDE.Messages;

/// <summary>The editor actions that can be triggered from outside the editor (menus, key bindings).</summary>
public enum EditorAction
{
    GoToDefinition,
    FindReferences,
    RenameSymbol,
    QuickOpen,
    WorkspaceSymbols,
    QuickActions,
    SignatureHelp,
    Completion,
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    SelectAll
}

/// <summary>Ask the editor pane to perform an action on the active document. Handled by EditorPaneView.</summary>
public sealed record EditorActionMessage(EditorAction Action);
