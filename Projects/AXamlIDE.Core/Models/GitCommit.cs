using System;
using System.Collections.Generic;

namespace AXamlIDE.Models;

/// <summary>What a label on a commit in the Git graph is.</summary>
public enum GitRefKind
{
    /// <summary>The checked-out branch ("HEAD -> main").</summary>
    CurrentBranch,
    Branch,
    RemoteBranch,
    Tag,
    /// <summary>HEAD when no branch is checked out.</summary>
    DetachedHead
}

/// <summary>A branch, tag or HEAD pointing at a commit, by its short name ("main", "origin/main", "v1.0").</summary>
public sealed record GitRef(string Name, GitRefKind Kind);

/// <summary>One commit from <c>git log</c>.</summary>
/// <param name="Parents">The parent hashes: none for the first commit, two (or more) for a merge.</param>
/// <param name="Body">The rest of the message after the subject line ("" when there's only a subject).</param>
public sealed record GitCommit(
    string Hash,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset Date,
    string Subject,
    IReadOnlyList<GitRef> Refs,
    string Body = "")
{
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;
    public bool IsMerge => Parents.Count > 1;
}

/// <summary>A file a commit changed, compared with its first parent.</summary>
public sealed record GitCommitFile(string RelativePath, GitFileState State, string? OriginalRelativePath = null);
