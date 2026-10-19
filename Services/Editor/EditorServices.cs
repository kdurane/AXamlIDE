using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Xaml;
using CommunityToolkit.Mvvm.Messaging;
using AXamlIDE.Editor.Highlighting;

namespace AXamlIDE.Services.Editor;

/// <summary>Everything an editor pane needs from outside, handed over once in Initialize().</summary>
public sealed record EditorServices(
    IEditorSettings Settings,
    XamlLanguageService Xaml,
    IDocumentHost Documents,
    IDialogService Dialogs,
    IClipboardService Clipboard,
    LanguageSessionService Languages,
    NavigationService Navigation,
    WorkspaceEditService WorkspaceEdits,
    DebugSnapshotTracker DebugSnapshots,
    EditorGrammars Grammars,
    IMessenger Messenger);
