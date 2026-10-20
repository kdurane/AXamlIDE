using AXamlIDE.Messages;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Commands;

/// <summary>
/// One command per editor action, for menus and the KeyMap to bind to. Each just sends an
/// <see cref="EditorActionMessage"/>, so callers never need a reference to the editor pane
/// (which matters once panels can live in other windows).
/// </summary>
public sealed class EditorCommands(IMessenger messenger)
{
    public IRelayCommand GoToDefinition { get; } = Make(messenger, EditorAction.GoToDefinition);
    public IRelayCommand FindReferences { get; } = Make(messenger, EditorAction.FindReferences);
    public IRelayCommand RenameSymbol { get; } = Make(messenger, EditorAction.RenameSymbol);
    public IRelayCommand QuickOpen { get; } = Make(messenger, EditorAction.QuickOpen);
    public IRelayCommand WorkspaceSymbols { get; } = Make(messenger, EditorAction.WorkspaceSymbols);
    public IRelayCommand QuickActions { get; } = Make(messenger, EditorAction.QuickActions);
    public IRelayCommand SignatureHelp { get; } = Make(messenger, EditorAction.SignatureHelp);
    public IRelayCommand Completion { get; } = Make(messenger, EditorAction.Completion);

    // The editor handles these keys itself (Ctrl+Z, Ctrl+C...); the commands are for the Edit menu.
    public IRelayCommand Undo { get; } = Make(messenger, EditorAction.Undo);
    public IRelayCommand Redo { get; } = Make(messenger, EditorAction.Redo);
    public IRelayCommand Cut { get; } = Make(messenger, EditorAction.Cut);
    public IRelayCommand Copy { get; } = Make(messenger, EditorAction.Copy);
    public IRelayCommand Paste { get; } = Make(messenger, EditorAction.Paste);
    public IRelayCommand SelectAll { get; } = Make(messenger, EditorAction.SelectAll);

    /// <summary>Debug > Add Watch (and the editor's menu while paused): the selection, or the expression at the caret.</summary>
    public IRelayCommand AddWatch { get; } = Make(messenger, EditorAction.AddWatch);
    public IRelayCommand InsertSnippet { get; } = Make(messenger, EditorAction.InsertSnippet);

    private static RelayCommand Make(IMessenger messenger, EditorAction action)
        => new(() => messenger.Send(new EditorActionMessage(action)));
}
