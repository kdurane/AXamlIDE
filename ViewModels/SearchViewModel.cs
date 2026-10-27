using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Search;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Search tool tab: find (and replace) text across the workspace. Open documents are
/// searched from their editor text, so unsaved changes are included.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject, IRecipient<FocusFindInFilesMessage>
{
    private readonly WorkspaceModel _workspace;
    private readonly DocumentManager _documents;
    private readonly IDialogService _dialog;
    private readonly IMessenger _messenger;

    public SearchViewModel(WorkspaceModel workspace, DocumentManager documents, IDialogService dialog, IMessenger messenger)
    {
        _workspace = workspace;
        _documents = documents;
        _dialog = dialog;
        _messenger = messenger;
        messenger.RegisterAll(this);
    }

    private ObservableCollection<OpenDocument> OpenDocuments => _documents.Documents;

    /// <summary>
    /// Raised when something asks for the find box to get focus; the view handles it. If no view
    /// exists yet (panels are created the first time they're shown) the request is kept until one
    /// asks for it with <see cref="TakePendingFocusRequest"/>.
    /// </summary>
    public event Action? FocusQueryRequested;

    private bool _focusPending;

    public void Receive(FocusFindInFilesMessage message)
    {
        if (FocusQueryRequested is null)
            _focusPending = true;
        else
            FocusQueryRequested.Invoke();
    }

    public bool TakePendingFocusRequest()
    {
        var pending = _focusPending;
        _focusPending = false;
        return pending;
    }

    /// <summary>Open a result in the editor and select the match.</summary>
    [RelayCommand]
    private void OpenResult(WorkspaceSearchResult? result)
    {
        if (result is not null)
            _messenger.Send(new NavigateToSearchResultMessage(result, SearchQuery));
    }

    private readonly WorkspaceSearchService _search = new();

    /// <summary>The results, one group per file.</summary>
    public ObservableCollection<SearchResultGroup> SearchResults { get; } = [];

    [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial string ReplaceText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial string SearchStatus { get; set; } = "Search the current workspace.";

    [RelayCommand]
    private async Task SearchWorkspace()
    {
        if (IsSearching)
            return;

        SearchResults.Clear();
        var query = SearchQuery;
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchStatus = "Enter text to search for.";
            return;
        }

        if (GetSearchRoots() is not { Count: > 0 } roots)
        {
            SearchStatus = "Open a folder or solution before searching.";
            return;
        }

        var openDocuments = SnapshotOpenTexts();

        IsSearching = true;
        SearchStatus = "Searching...";
        try
        {
            var summary = await _search.SearchAsync(roots, query, openDocuments);
            var groups = SearchResultGroup.Build(roots[0], summary.Results, query);
            foreach (var group in groups)
                SearchResults.Add(group);

            var status = summary.ResultLimitReached
                ? $"Showing the first {summary.Results.Count} results in {groups.Count} file(s)."
                : $"{summary.Results.Count} result(s) in {groups.Count} file(s).";
            if (summary.SkippedLargeFiles > 0)
                status += $" Skipped {summary.SkippedLargeFiles} file(s) larger than 2 MB.";
            if (summary.UnreadableFiles > 0)
                status += $" Couldn't read {summary.UnreadableFiles} file(s) or folder(s).";
            SearchStatus = status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SearchStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task ReplaceWorkspaceAsync()
    {
        var plan = await PrepareReplaceAsync();
        if (plan is null || plan.LimitReached || plan.MatchCount == 0)
            return;

        if (!await _dialog.ConfirmWorkspaceReplaceAsync(plan))
            return;

        var summary = ApplyReplacePlan(plan);
        var message = $"Replaced {summary.ReplacedMatches} match(es) in {summary.ReplacedFiles} file(s).";
        if (summary.ModifiedOpenDocuments > 0)
            message += $" {summary.ModifiedOpenDocuments} open document(s) now have unsaved changes.";
        if (summary.FailedFiles > 0)
            message += $" {summary.FailedFiles} file(s) changed since preview or couldn't be written.";
        SearchStatus = message;
        await SearchWorkspace();
        SearchStatus = message;
    }

    private async Task<WorkspaceReplacePlan?> PrepareReplaceAsync()
    {
        if (IsSearching)
        {
            SearchStatus = "Wait for the current search to finish before replacing.";
            return null;
        }

        var query = SearchQuery;
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchStatus = "Enter text to search for before replacing.";
            return null;
        }

        if (GetSearchRoots() is not { Count: > 0 } roots)
        {
            SearchStatus = "Open a folder or solution before replacing.";
            return null;
        }

        var openDocuments = SnapshotOpenTexts();

        IsSearching = true;
        SearchStatus = "Preparing replace...";
        try
        {
            var plan = await _search.PrepareReplaceAsync(roots, query, ReplaceText, openDocuments);
            SearchStatus = plan.LimitReached
                ? "More than 5,000 matches found. Narrow the search before replacing."
                : $"{plan.MatchCount} match(es) in {plan.Files.Count} file(s) ready to replace.";
            return plan;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SearchStatus = $"Replace preparation failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private WorkspaceReplaceSummary ApplyReplacePlan(WorkspaceReplacePlan plan)
    {
        var openDocuments = OpenDocuments.ToDictionary(
            document => Path.GetFullPath(document.Path),
            document => (ITextBuffer)document,
            StringComparer.OrdinalIgnoreCase);
        return _search.ApplyReplacePlan(plan, openDocuments);
    }

    private IReadOnlyList<string> GetSearchRoots() => _workspace.SearchRoots;

    private Dictionary<string, string> SnapshotOpenTexts()
        => OpenDocuments.ToDictionary(
            document => Path.GetFullPath(document.Path),
            document => document.Document.Text,
            StringComparer.OrdinalIgnoreCase);
}
