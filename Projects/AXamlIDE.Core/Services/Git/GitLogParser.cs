using System;
using System.Collections.Generic;
using System.Globalization;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Git;

/// <summary>
/// Reads the output of <c>git log</c> (in <see cref="Format"/>) and <c>git diff-tree --name-status -z</c>.
/// </summary>
public static class GitLogParser
{
    private const char FieldSeparator = '\x1f';    // %x1f: can't appear in a name or a subject
    private const char RecordSeparator = '\x1e';   // %x1e: ends each commit

    /// <summary>
    /// The <c>--format</c> for <see cref="ParseLog"/>: hash, parents, author, email, date, refs, subject, body. Use it
    /// with <c>--decorate=full</c>, so the refs say what they are ("refs/remotes/origin/main", not "origin/main").
    /// </summary>
    public const string Format = "%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%D%x1f%s%x1f%b%x1e";

    public static IReadOnlyList<GitCommit> ParseLog(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var record in output.Split(RecordSeparator))
        {
            var fields = record.TrimStart('\n', '\r').Split(FieldSeparator);
            if (fields.Length < 7 || fields[0].Length == 0)
                continue;

            commits.Add(new GitCommit(
                Hash: fields[0],
                Parents: fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                AuthorName: fields[2],
                AuthorEmail: fields[3],
                Date: DateTimeOffset.TryParse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : default,
                Subject: fields[6],
                Refs: ParseRefs(fields[5]),
                Body: fields.Length > 7 ? fields[7].Trim() : ""));
        }
        return commits;
    }

    /// <summary>
    /// The refs from %D with --decorate=full: "HEAD -> refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1".
    /// Remote HEAD aliases ("refs/remotes/origin/HEAD") and the stash are left out.
    /// </summary>
    public static IReadOnlyList<GitRef> ParseRefs(string decoration)
    {
        var refs = new List<GitRef>();
        foreach (var part in decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == "HEAD")
                refs.Add(new GitRef("HEAD", GitRefKind.DetachedHead));
            else if (part.StartsWith("HEAD -> ", StringComparison.Ordinal))
                refs.Add(new GitRef(ShortName(part["HEAD -> ".Length..]), GitRefKind.CurrentBranch));
            else if (part.StartsWith("tag: ", StringComparison.Ordinal))
                refs.Add(new GitRef(ShortName(part["tag: ".Length..]), GitRefKind.Tag));
            else if (part.StartsWith("refs/heads/", StringComparison.Ordinal))
                refs.Add(new GitRef(ShortName(part), GitRefKind.Branch));
            else if (part.StartsWith("refs/remotes/", StringComparison.Ordinal) && !part.EndsWith("/HEAD", StringComparison.Ordinal))
                refs.Add(new GitRef(ShortName(part), GitRefKind.RemoteBranch));
        }
        return refs;
    }

    /// <summary>"refs/heads/main" → "main", "refs/remotes/origin/main" → "origin/main", "refs/tags/v1" → "v1".</summary>
    private static string ShortName(string fullName)
    {
        foreach (var prefix in (string[])["refs/heads/", "refs/remotes/", "refs/tags/"])
            if (fullName.StartsWith(prefix, StringComparison.Ordinal))
                return fullName[prefix.Length..];
        return fullName;
    }

    /// <summary>
    /// The files from <c>git diff-tree -r -M --name-status -z</c>: a status then its path, each ending in NUL, and a
    /// rename or copy ("R100", "C75") followed by the old path and then the new one.
    /// </summary>
    public static IReadOnlyList<GitCommitFile> ParseNameStatus(string output)
    {
        var files = new List<GitCommitFile>();
        var parts = output.Split('\0');
        var i = 0;
        while (i < parts.Length)
        {
            var status = parts[i++];
            if (status.Length == 0 || i >= parts.Length)
                continue;

            if (status[0] is 'R' or 'C' && i + 1 < parts.Length)
            {
                var original = parts[i++];
                var path = parts[i++];
                files.Add(new GitCommitFile(path, status[0] == 'R' ? GitFileState.Renamed : GitFileState.Added, original));
            }
            else
            {
                files.Add(new GitCommitFile(parts[i++], StateOf(status[0])));
            }
        }
        return files;
    }

    private static GitFileState StateOf(char status) => status switch
    {
        'A' => GitFileState.Added,
        'D' => GitFileState.Deleted,
        'U' => GitFileState.Conflicted,
        _ => GitFileState.Modified   // M, T (type changed)
    };
}
