using System;
using System.Collections.Generic;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Git;

/// <summary>
/// Reads the output of <c>git status --porcelain=v2 -z --branch</c> (see "git help status", "Porcelain
/// Format Version 2"). Each record ends with a NUL; a rename record is followed by its original path as a
/// record of its own. Paths are relative to the repository root and are never quoted with -z.
/// </summary>
public static class GitStatusParser
{
    public static GitStatus Parse(string output, string repositoryRoot)
    {
        string? branch = null, headCommit = null, upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<GitChange>();
        var ignored = new List<string>();

        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            switch (record[0])
            {
                case '#':
                    ReadHeader(record, ref branch, ref headCommit, ref upstream, ref ahead, ref behind);
                    break;

                // 1 XY sub mH mI mW hH hI path
                case '1':
                {
                    var fields = record.Split(' ', 9);
                    changes.Add(Change(repositoryRoot, fields[8], fields[1], original: null, fields[6], fields[7]));
                    break;
                }

                // 2 XY sub mH mI mW hH hI Xscore path, then the original path as the next record
                case '2':
                {
                    var fields = record.Split(' ', 10);
                    var original = i + 1 < records.Length ? records[++i] : null;
                    changes.Add(Change(repositoryRoot, fields[9], fields[1], original, fields[6], fields[7]));
                    break;
                }

                // u XY sub m1 m2 m3 mW h1 h2 h3 path
                case 'u':
                {
                    var fields = record.Split(' ', 11);
                    changes.Add(new GitChange(fields[10], GitStatus.ToFullPath(repositoryRoot, fields[10]),
                        GitFileState.Conflicted, GitFileState.Modified));
                    break;
                }

                case '?':
                    changes.Add(new GitChange(record[2..], GitStatus.ToFullPath(repositoryRoot, record[2..]),
                        GitFileState.None, GitFileState.Untracked));
                    break;

                case '!':
                    ignored.Add(record[2..]);
                    break;
            }
        }

        return new GitStatus(repositoryRoot, branch, headCommit, upstream, ahead, behind, changes, ignored);
    }

    private static void ReadHeader(string record, ref string? branch, ref string? headCommit, ref string? upstream,
        ref int ahead, ref int behind)
    {
        var fields = record.Split(' ');
        switch (fields[1])
        {
            case "branch.oid":
                headCommit = fields[2] == "(initial)" ? null : fields[2];
                break;
            case "branch.head":
                branch = fields[2] == "(detached)" ? null : fields[2];
                break;
            case "branch.upstream":
                upstream = fields[2];
                break;
            case "branch.ab" when fields.Length >= 4:
                int.TryParse(fields[2].TrimStart('+'), out ahead);
                int.TryParse(fields[3].TrimStart('-'), out behind);
                break;
        }
    }

    private static GitChange Change(string repositoryRoot, string path, string xy, string? original,
        string headObject, string indexObject)
        => new(path, GitStatus.ToFullPath(repositoryRoot, path), StateOf(xy[0]), StateOf(xy[1]), original,
            headObject, indexObject);

    /// <summary>One side of the XY code ('.' means unchanged).</summary>
    private static GitFileState StateOf(char code) => code switch
    {
        'M' or 'T' => GitFileState.Modified,
        'A' => GitFileState.Added,
        'D' => GitFileState.Deleted,
        'R' or 'C' => GitFileState.Renamed,
        _ => GitFileState.None
    };
}
