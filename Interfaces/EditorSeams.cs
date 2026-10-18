using System.Collections.Generic;
using System.Threading.Tasks;
using AvaloniaEdit.TextMate;
using AXamlIDE.Lsp;
using AXamlIDE.Models;
using AXamlIDE.Services;
using AXamlIDE.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.Interfaces;

public interface IDocumentHost
{
    OpenDocument Open(string path);
    OpenDocument OpenReadOnly(string path, string text, string languageId);
    void Activate(OpenDocument document);
    Task<IReadOnlyList<string>> GetWorkspaceFilesAsync();
}
public static class DocumentHostExtensions
{
    /// <summary>Was <c>DocTabs.SelectedItem = ViewModel.OpenDocument(path)</c>.</summary>
    public static OpenDocument OpenAndActivate(this IDocumentHost host, string path)
    {
        var document = host.Open(path);
        host.Activate(document);
        return document;
    }
}

/// <summary>Everything the pane needs from outside, handed over once in Initialize().</summary>
public sealed record EditorServices(IEditorSettings Settings, XamlLanguageService Xaml, IDocumentHost Documents,
    IDialogService Dialogs,
    IMessenger Messenger);

public sealed record NavigateToProblemMessage(ProblemItem Problem);
public sealed record NavigateToDebugLocationMessage(string Path, int Line);
public sealed record NavigateToSearchResultMessage(WorkspaceSearchResult Result, string SearchQuery);
public sealed record NavigateToOutlineItemMessage(DocumentOutlineItem Item); // use your outline item type
public sealed record NavigateToLocationMessage(LspLocation Location);

// Into the editor: state changes it reflects. Sent by view models.

/// <summary>Null snapshot = running/ended.</summary>
public sealed record DebugSnapshotChangedMessage(StopSnapshot? Snapshot);
public sealed record DiagnosticsChangedMessage(OpenDocument Document);

// Out of the editor.

public sealed record StatusMessage(string Text);
/// <summary>Caret moved or diagnostics changed; whoever owns the status bar refreshes it.</summary>
public sealed record EditorStateChangedMessage;
public sealed record EditorThemeAppliedMessage(TextMate.Installation Installation);