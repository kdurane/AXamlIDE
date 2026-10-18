using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Documents;
using AXamlIDE.Shared;
using AXamlIDE.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.Search;

public partial class SearchViewModel(ObservableCollection<ProjectTreeNode> roots, ObservableCollection<OpenDocument> documents, IDialogService dialog) : ObservableObject
{
    private readonly WorkspaceSearchService _search = new();

    public ObservableCollection<WorkspaceSearchResult> SearchResults { get; } = [];

    [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial string ReplaceText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial string SearchStatus { get; set; } = "Search the current workspace.";

    public async Task<IReadOnlyList<string>> GetWorkspaceFilesAsync()
    {
        if (GetRootPath() is not { } rootPath)
            return [];
        return await _search.ListWorkspaceFilesAsync(rootPath);
    }

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

        if (GetRootPath() is not { } rootPath)
        {
            SearchStatus = "Open a folder or solution before searching.";
            return;
        }

        var openDocuments = SnapshotOpenTexts();

        IsSearching = true;
        SearchStatus = "Searching...";
        try
        {
            var summary = await _search.SearchAsync(rootPath, query, openDocuments);
            foreach (var result in summary.Results)
                SearchResults.Add(result);

            var status = summary.ResultLimitReached
                ? $"Showing the first {summary.Results.Count} results."
                : $"{summary.Results.Count} result(s).";
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

        if (!await dialog.ConfirmWorkspaceReplaceAsync(plan))
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

        if (GetRootPath() is not { } rootPath)
        {
            SearchStatus = "Open a folder or solution before replacing.";
            return null;
        }

        var openDocuments = SnapshotOpenTexts();

        IsSearching = true;
        SearchStatus = "Preparing replace...";
        try
        {
            var plan = await _search.PrepareReplaceAsync(rootPath, query, ReplaceText, openDocuments);
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
        var openDocuments = documents.ToDictionary(
            document => Path.GetFullPath(document.Path),
            document => document,
            StringComparer.OrdinalIgnoreCase);
        return _search.ApplyReplacePlan(plan, openDocuments);
    }

    private string? GetRootPath()
    {
        if (roots.FirstOrDefault() is not { } root)
            return null;
        return root.Kind == NodeKind.Solution
            ? Path.GetDirectoryName(root.FullPath)!
            : root.FullPath;
    }

    private Dictionary<string, string> SnapshotOpenTexts()
        => documents.ToDictionary(
            document => Path.GetFullPath(document.Path),
            document => document.Document.Text,
            StringComparer.OrdinalIgnoreCase);
}
