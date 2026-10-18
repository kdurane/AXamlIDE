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

    private static IRelayCommand Make(IMessenger messenger, EditorAction action)
        => new RelayCommand(() => messenger.Send(new EditorActionMessage(action)));
}
