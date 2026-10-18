using AXamlIDE.Documents;
using AXamlIDE.Settings;
using AXamlIDE.Shared;
using AXamlIDE.Xaml;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Editor;

/// <summary>Everything the editor pane needs from outside, handed over once in Initialize().</summary>
public sealed record EditorServices(
    IEditorSettings Settings,
    XamlLanguageService Xaml,
    IDocumentHost Documents,
    IDialogService Dialogs,
    IMessenger Messenger);
