using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AXamlIDE.Models;

/// <summary>How a file (or a folder, from what's inside it) differs from the last commit, for colouring.</summary>
public enum GitFileState
{
    None,
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted,
    Ignored
}

/// <summary>
/// One changed file from <c>git status</c>. A file can be both staged and changed again after staging,
/// so it has a state for each side: <see cref="IndexState"/> (staged, compared with HEAD) and
/// <see cref="WorkTreeState"/> (not staged, compared with the index).
/// </summary>
public sealed record GitChange(
    string RelativePath,
    string FullPath,
    GitFileState IndexState,
    GitFileState WorkTreeState,
    string? OriginalRelativePath = null)
{
    public bool IsConflicted => IndexState == GitFileState.Conflicted;
    public bool IsStaged => IndexState is not (GitFileState.None or GitFileState.Conflicted);
    public bool HasUnstagedChanges => WorkTreeState != GitFileState.None || IsConflicted;

    /// <summary>The single state shown for the file (the explorer's colour).</summary>
    public GitFileState State => IsConflicted ? GitFileState.Conflicted
        : WorkTreeState == GitFileState.Untracked ? GitFileState.Untracked
        : IndexState == GitFileState.Deleted || WorkTreeState == GitFileState.Deleted ? GitFileState.Deleted
        : IndexState == GitFileState.Added ? GitFileState.Added
        : IndexState == GitFileState.Renamed ? GitFileState.Renamed
        : GitFileState.Modified;
}

/// <summary>
/// A repository's state from one <c>git status</c>: the branch and every changed or ignored path, with
/// lookups by full path for the explorer (a file's state, and a folder's from the files inside it).
/// </summary>
public sealed class GitStatus
{
    /// <summary>Paths compare like the file system: case-sensitively on Linux.</summary>
    public static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly Dictionary<string, GitChange> _byPath;
    private readonly Dictionary<string, GitFileState> _folders;
    private readonly HashSet<string> _ignored;

    public GitStatus(
        string repositoryRoot,
        string? branch,
        string? upstream,
        int ahead,
        int behind,
        IReadOnlyList<GitChange> changes,
        IReadOnlyList<string> ignoredRelativePaths)
    {
        RepositoryRoot = Path.TrimEndingDirectorySeparator(repositoryRoot);
        Branch = branch;
        Upstream = upstream;
        Ahead = ahead;
        Behind = behind;
        Changes = changes;

        _byPath = changes.GroupBy(change => change.FullPath, PathComparer)
            .ToDictionary(group => group.Key, group => group.Last(), PathComparer);
        _ignored = ignoredRelativePaths
            .Select(path => Path.TrimEndingDirectorySeparator(FullPathOf(path)))
            .ToHashSet(PathComparer);

        _folders = new Dictionary<string, GitFileState>(PathComparer);
        foreach (var change in changes)
            for (var folder = Path.GetDirectoryName(change.FullPath);
                 folder is not null && folder.Length >= RepositoryRoot.Length;
                 folder = Path.GetDirectoryName(folder))
                _folders[folder] = Stronger(_folders.GetValueOrDefault(folder), change.State);
    }

    public string RepositoryRoot { get; }

    /// <summary>The checked-out branch, or null when HEAD is detached.</summary>
    public string? Branch { get; }

    public string? Upstream { get; }
    public int Ahead { get; }
    public int Behind { get; }
    public IReadOnlyList<GitChange> Changes { get; }

    public IEnumerable<GitChange> Staged => Changes.Where(change => change.IsStaged);
    public IEnumerable<GitChange> Unstaged => Changes.Where(change => change.HasUnstagedChanges);

    /// <summary>A path inside the repository, from one relative to its root (as git prints it, with '/').</summary>
    public string FullPathOf(string relativePath) => ToFullPath(RepositoryRoot, relativePath);

    public static string ToFullPath(string repositoryRoot, string relativePath)
        => Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public GitChange? ChangeFor(string fullPath) => _byPath.GetValueOrDefault(fullPath);

    /// <summary>A file's state: its change, else Ignored if it or a folder above it is ignored.</summary>
    public GitFileState StateOfFile(string fullPath)
        => _byPath.TryGetValue(fullPath, out var change) ? change.State
            : IsIgnored(fullPath) ? GitFileState.Ignored
            : GitFileState.None;

    /// <summary>A folder's state: Ignored, or the strongest state of the files changed inside it.</summary>
    public GitFileState StateOfFolder(string fullPath)
    {
        var folder = Path.TrimEndingDirectorySeparator(fullPath);
        return IsIgnored(folder) ? GitFileState.Ignored : _folders.GetValueOrDefault(folder);
    }

    private bool IsIgnored(string fullPath)
    {
        for (var path = fullPath;
             path is not null && path.Length > RepositoryRoot.Length;
             path = Path.GetDirectoryName(path))
            if (_ignored.Contains(path))
                return true;
        return false;
    }

    /// <summary>Which of two states a folder shows: conflicts first, then edits, then new files.</summary>
    public static GitFileState Stronger(GitFileState a, GitFileState b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(GitFileState state) => state switch
    {
        GitFileState.Conflicted => 5,
        GitFileState.Modified or GitFileState.Deleted or GitFileState.Renamed => 4,
        GitFileState.Added => 3,
        GitFileState.Untracked => 2,
        GitFileState.Ignored => 1,
        _ => 0
    };
}
