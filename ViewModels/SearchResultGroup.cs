using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AXamlIDE.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One file in the Search tab's results, VS Code style: the file name, its folder relative to the workspace (dimmed)
/// and the match count, with the matching lines under it (collapsible).
/// </summary>
public sealed partial class SearchResultGroup : ObservableObject
{
    public SearchResultGroup(string rootPath, string filePath, IReadOnlyList<SearchMatchItem> matches)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        var folder = Path.GetRelativePath(rootPath, Path.GetDirectoryName(filePath) ?? rootPath);
        Folder = folder == "." ? "" : folder;
        Matches = matches;
    }

    public string FilePath { get; }
    public string FileName { get; }

    /// <summary>The folder relative to the workspace root ("" at the root).</summary>
    public string Folder { get; }

    public IReadOnlyList<SearchMatchItem> Matches { get; }
    public int Count => Matches.Count;

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    /// <summary>Groups results by file, keeping the order they were found in.</summary>
    public static List<SearchResultGroup> Build(string rootPath, IEnumerable<WorkspaceSearchResult> results, string query) =>
    [
        .. results.GroupBy(result => result.FilePath, StringComparer.Ordinal)
            .Select(file => new SearchResultGroup(rootPath, file.Key,
            [
                .. file.GroupBy(result => result.LineNumber)
                    .SelectMany(line => line.Select((result, nth) => SearchMatchItem.Create(result, query, nth)))
            ]))
    ];
}

/// <summary>A matching line: the text around the match, with the match itself highlighted.</summary>
public sealed record SearchMatchItem(WorkspaceSearchResult Result, string Before, string Match, string After)
{
    private const int MaxBefore = 40;   // long lines are cut before the match so the match stays in view

    public int LineNumber => Result.LineNumber;

    /// <summary>
    /// Splits the result's (trimmed) line around the <paramref name="nth"/> occurrence of the query on it: results
    /// only carry the column in the untrimmed line.
    /// </summary>
    public static SearchMatchItem Create(WorkspaceSearchResult result, string query, int nth)
    {
        var text = result.LineText;
        var at = -1;
        for (var i = 0; i <= nth; i++)
        {
            var next = text.IndexOf(query, at + 1, StringComparison.OrdinalIgnoreCase);
            if (next < 0)
                break;
            at = next;
        }
        if (at < 0 || query.Length == 0)
            return new SearchMatchItem(result, text, "", "");

        var before = text[..at];
        if (before.Length > MaxBefore)
            before = "…" + before[^MaxBefore..].TrimStart();
        return new SearchMatchItem(result, before, text.Substring(at, query.Length), text[(at + query.Length)..]);
    }
}
