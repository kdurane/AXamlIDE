using System;
using System.IO;
using System.Linq;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Remembers each workspace's session in its <c>.axamlide/</c> folder (WorkspaceStateStore): the files
/// that were open (caret, scroll, preview layout), which was selected, and the dock sizes.
///
/// Restores it when a workspace opens. Saves it when the workspace is closed (MainWindowViewModel.CloseSolution
/// calls <see cref="Close"/> before closing the documents), when another workspace is opened over it, and
/// when the app exits (MainWindow calls <see cref="Save"/>).
/// </summary>
public sealed class WorkspaceSession
{
    private readonly WorkspaceModel _workspace;
    private readonly DocumentManager _documents;
    private readonly DockLayoutViewModel _docking;
    private readonly WorkspaceStateStore _store;
    private readonly IMessenger _messenger;
    private string? _folder;   // the workspace whose session is live

    public WorkspaceSession(
        WorkspaceViewModel workspaceViewModel,
        WorkspaceModel workspace,
        DocumentManager documents,
        DockLayoutViewModel docking,
        WorkspaceStateStore store,
        IMessenger messenger)
    {
        _workspace = workspace;
        _documents = documents;
        _docking = docking;
        _store = store;
        _messenger = messenger;
        workspaceViewModel.Opened += OnWorkspaceOpened;
    }

    private void OnWorkspaceOpened()
    {
        if (_workspace.RootDirectory is not { } folder || string.Equals(folder, _folder, StringComparison.Ordinal))
            return;   // a refresh of the same workspace

        Save();
        _folder = folder;
        Restore(folder);
    }

    /// <summary>Save the live workspace's session (no-op when none is open).</summary>
    public void Save()
    {
        if (_folder is not { } folder)
            return;

        var files = _documents.Documents.Where(document => !document.IsReadOnly).Select(document =>
        {
            var tab = _docking.TabFor(document);
            return new OpenFileState
            {
                Path = document.Path,
                CaretOffset = document.CaretOffset,
                VerticalOffset = document.VerticalOffset,
                PreviewMode = tab?.PreviewMode.ToString(),
                PreviewSplit = tab?.PreviewSplit.ToString(),
                PreviewFirst = tab?.PreviewFirst ?? false
            };
        }).ToList();

        var state = new WorkspaceState
        {
            OpenFiles = files,
            ActiveFile = _documents.Active is { IsReadOnly: false } active ? active.Path : null,
            Layout = _docking.CaptureLayout()
        };

        if (_store.Save(folder, state) is { } error)
            _messenger.Send(new StatusMessage(error));
    }

    /// <summary>The workspace is being closed: save its session and stop tracking it.</summary>
    public void Close()
    {
        Save();
        _folder = null;
    }

    private void Restore(string folder)
    {
        var state = _store.Load(folder);
        _docking.ApplyLayout(state.Layout);

        OpenDocument? active = null;
        foreach (var file in state.OpenFiles)
        {
            if (!File.Exists(file.Path))
                continue;

            OpenDocument document;
            try
            {
                document = _documents.Open(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            document.CaretOffset = Math.Clamp(file.CaretOffset, 0, document.Document.TextLength);
            document.VerticalOffset = Math.Max(0, file.VerticalOffset);
            if (_docking.TabFor(document) is { } tab)
            {
                if (Enum.TryParse<PreviewMode>(file.PreviewMode, out var mode)) tab.PreviewMode = mode;
                if (Enum.TryParse<PreviewSplit>(file.PreviewSplit, out var split)) tab.PreviewSplit = split;
                tab.PreviewFirst = file.PreviewFirst;
            }

            if (string.Equals(file.Path, state.ActiveFile, StringComparison.Ordinal) || active is null)
                active = document;
        }

        if (active is not null)
            _documents.Active = active;
    }
}
