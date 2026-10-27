using System;
using System.Collections.Generic;
using System.IO;
using AXamlIDE.Models;
using AXamlIDE.Services.Git;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.ViewModels;

/// <summary>
/// A commit's row in the Git graph: its part of the graph, subject, branch and tag labels, and author. Clicking it
/// opens it, listing the files it changed underneath (<see cref="GitGraphFileItem"/>), as VS Code's graph does.
/// </summary>
public sealed partial class GitGraphCommitItem(GitCommit commit, GitGraphRow row, bool isHead) : ObservableObject
{
    public GitCommit Commit { get; } = commit;
    public GitGraphRow Row { get; } = row;

    /// <summary>The checked-out commit: its dot has a ring round it.</summary>
    public bool IsHead { get; } = isHead;

    public string Subject => Commit.Subject;
    public IReadOnlyList<GitRef> Refs => Commit.Refs;

    /// <summary>"Ann, 2 hours ago".</summary>
    public string AuthorAndDate => $"{Commit.AuthorName}, {GitDates.Ago(Commit.Date)}";

    public string ToolTip => $"{Commit.Subject}\n\n{Commit.AuthorName} <{Commit.AuthorEmail}>\n"
                             + $"{Commit.Date.LocalDateTime:f} ({GitDates.Ago(Commit.Date)})\n{Commit.Hash}";

    /// <summary>Its changed files are listed under it.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>The files it changed, once read (they don't change, so they're kept).</summary>
    public IReadOnlyList<GitGraphFileItem>? Files { get; set; }
}

/// <summary>A file changed by a commit, listed under the commit's row in the graph; clicking it opens the diff.</summary>
public sealed record GitGraphFileItem(GitGraphCommitItem Owner, GitCommitFile File)
{
    /// <summary>The graph's lines carry on through the file rows to the commits below.</summary>
    public GitGraphRow Row { get; } = Owner.Row.Continuation();

    public string FileName => Path.GetFileName(File.RelativePath);
    public string Folder => Path.GetDirectoryName(File.RelativePath)?.Replace('\\', '/') ?? "";
    public GitFileState State => File.State;

    public string ToolTip => File.OriginalRelativePath is { } original
        ? $"{File.RelativePath} (renamed from {original})"
        : File.RelativePath;
}

/// <summary>The last row of the graph when there are older commits than it shows.</summary>
public sealed class GitGraphLoadMoreItem;

/// <summary>Dates as VS Code's graph shows them.</summary>
public static class GitDates
{
    /// <summary>"just now", "5 minutes ago", "yesterday", "3 weeks ago"...</summary>
    public static string Ago(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var age = (now ?? DateTimeOffset.Now) - date;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? Count((int)age.TotalMinutes, "minute")
            : age.TotalDays < 1 ? Count((int)age.TotalHours, "hour")
            : age.TotalDays < 2 ? "yesterday"
            : age.TotalDays < 7 ? Count((int)age.TotalDays, "day")
            : age.TotalDays < 31 ? Count((int)(age.TotalDays / 7), "week")
            : age.TotalDays < 365 ? Count((int)(age.TotalDays / 30.4), "month")
            : Count((int)(age.TotalDays / 365.25), "year");

        static string Count(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";
    }
}
