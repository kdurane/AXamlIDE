using System;
using System.Collections.Generic;
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
/// that were open (caret, scroll, collapsed folds, preview layout), which was selected, the dock sizes, and the toolbar's startup
/// project and configuration. Closed files keep only their collapsed folds, so a file reopens folded as it was left.
/// Breakpoints, in open and closed files, are remembered too unless Settings > Debugging says not to
/// (closed files' breakpoints are held by <see cref="ClosedFileBreakpoints"/> while the workspace is open).
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
    private readonly RunViewModel _run;
    private readonly WorkspaceStateStore _store;
    private readonly ClosedFileBreakpoints _closedBreakpoints;
    private readonly Services.Settings.IEditorSettings _settings;

    private readonly WorkspaceViewModel _tree;
    private readonly IMessenger _messenger;
    private string? _folder;   // the workspace whose session is live

    // Closed files' collapsed fold lines, by path (open files carry theirs on the document).
    private Dictionary<string, List<int>> _closedFolds = new(StringComparer.Ordinal);

    public WorkspaceSession(
        WorkspaceViewModel workspaceViewModel,
        WorkspaceModel workspace,
        DocumentManager documents,
        DockLayoutViewModel docking,
        RunViewModel run,
        WorkspaceStateStore store,
        ClosedFileBreakpoints closedBreakpoints,
        Services.Settings.IEditorSettings settings,
        IMessenger messenger)
    {
        _workspace = workspace;
        _documents = documents;
        _docking = docking;
        _run = run;
        _store = store;
        _closedBreakpoints = closedBreakpoints;
        _settings = settings;
        _messenger = messenger;
        _tree = workspaceViewModel;
        workspaceViewModel.Opened += OnWorkspaceOpened;
        documents.Opened += OnDocumentOpened;
        documents.Closing += OnDocumentClosing;
    }

    private void OnDocumentOpened(OpenDocument document)
    {
        if (_closedFolds.Remove(document.Path, out var folds))
            document.RestoreFolds(folds);
    }

    private void OnDocumentClosing(OpenDocument document)
    {
        // Not while the workspace itself closes (its session is already saved) or for read-only views.
        if (_folder is not null && !document.IsReadOnly && document.CollapsedFolds is { } folds)
            _closedFolds[document.Path] = [.. folds];
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
                PreviewFirst = tab?.PreviewFirst ?? false,
                CollapsedFolds = document.CollapsedFolds?.ToList()
            };
        }).ToList();

        var state = new WorkspaceState
        {
            OpenFiles = files,
            ActiveFile = _documents.Active is { IsReadOnly: false } active ? active.Path : null,
            Layout = _docking.CaptureLayout(),
            StartupProject = _run.StartupProjectPath,
            Configuration = _run.Configuration,
            ExpandedNodes = _tree.ExpandedPaths(),
            ClosedFileFolds = _closedFolds
                .Where(pair => !files.Any(file => string.Equals(file.Path, pair.Key, StringComparison.Ordinal))
                               && File.Exists(pair.Key))   // forget deleted files
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Breakpoints = _settings.Features.RememberBreakpoints ? AllBreakpoints() : []
        };

        if (_store.Save(folder, state) is { } error)
            _messenger.Send(new StatusMessage(error));
    }

    /// <summary>Every breakpoint: the open files' (as they are now) and the closed files' (of files that still exist).</summary>
    private List<BreakpointState> AllBreakpoints()
    {
        var open = _documents.Documents.Where(document => !document.IsReadOnly)
            .SelectMany(document => document.Breakpoints.Select(breakpoint => (document.Path, Breakpoint: breakpoint)));
        var closed = _closedBreakpoints.All.Where(file => File.Exists(file.Path))
            .SelectMany(file => file.Breakpoints.Select(breakpoint => (file.Path, Breakpoint: breakpoint)));
        return
        [
            .. open.Concat(closed).Select(item => new BreakpointState
            {
                Path = item.Path,
                Line = item.Breakpoint.Line,
                IsEnabled = item.Breakpoint.IsEnabled,
                Condition = item.Breakpoint.Condition,
                HitCondition = item.Breakpoint.HitCondition
            })
        ];
    }

    /// <summary>The workspace is being closed: save its session and stop tracking it.</summary>
    public void Close()
    {
        Save();
        _folder = null;
        _closedFolds.Clear();
        _closedBreakpoints.End();   // before its files close, so they don't hand their breakpoints over
    }

    private void Restore(string folder)
    {
        var state = _store.Load(folder);
        _closedFolds = new Dictionary<string, List<int>>(state.ClosedFileFolds, StringComparer.Ordinal);
        // Before the files open, so each takes its own back as it does.
        _closedBreakpoints.Begin(_settings.Features.RememberBreakpoints
            ? state.Breakpoints.Select(b => (b.Path, new BreakpointInfo(b.Line, b.IsEnabled, b.Condition, b.HitCondition)))
            : []);
        _docking.ApplyLayout(state.Layout);
        _ = _tree.RestoreExpandedAsync(state.ExpandedNodes);
        _run.RestoreStartup(state.StartupProject, state.Configuration);

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
            if (file.CollapsedFolds is { } folds)
                document.RestoreFolds(folds);
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
