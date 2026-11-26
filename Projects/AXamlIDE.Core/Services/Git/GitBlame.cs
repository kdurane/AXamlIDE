using System;
using System.Collections.Generic;
using System.Globalization;

namespace AXamlIDE.Services.Git;

/// <summary>One line's last change: the commit, its author and when it was made. Not committed: an edit not in a commit yet.</summary>
public sealed record GitBlameLine(string Commit, string Author, DateTimeOffset Time, string Summary)
{
    public bool IsCommitted => Commit.Trim('0').Length > 0;
}

/// <summary>Reading <c>git blame --porcelain</c>, and saying how long ago something was, as Visual Studio's CodeLens does.</summary>
public static class GitBlame
{
    /// <summary>
    /// The lines, in order. Porcelain gives each line a header ("&lt;commit&gt; &lt;original line&gt; &lt;final line&gt;
    /// [&lt;count&gt;]"), the commit's details (author, author-time...) the first time a commit appears, then the
    /// line's text after a tab.
    /// </summary>
    public static IReadOnlyList<GitBlameLine> Parse(string porcelain)
    {
        var commits = new Dictionary<string, (string Author, long Time, string Summary)>(StringComparer.Ordinal);
        var lines = new List<GitBlameLine>();
        string? commit = null;
        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('\t'))
            {
                if (commit is not null)
                {
                    var details = commits.GetValueOrDefault(commit, ("", 0, ""));
                    lines.Add(new GitBlameLine(commit, details.Author, DateTimeOffset.FromUnixTimeSeconds(details.Time), details.Summary));
                }
                commit = null;
                continue;
            }

            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? "" : line[(space + 1)..];
            if (commit is null)
            {
                if (key.Length == 40 && IsHex(key))
                {
                    commit = key;
                    commits.TryAdd(commit, ("", 0, ""));
                }
                continue;
            }

            var known = commits[commit];
            commits[commit] = key switch
            {
                "author" => known with { Author = value },
                "author-time" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var time) => known with { Time = time },
                "summary" => known with { Summary = value },
                _ => known
            };
        }
        return lines;
    }

    /// <summary>The most recent change among lines <paramref name="first"/> to <paramref name="last"/> (1-based), or null.</summary>
    public static GitBlameLine? LastChange(IReadOnlyList<GitBlameLine> lines, int first, int last)
    {
        GitBlameLine? latest = null;
        for (var line = Math.Max(1, first); line <= Math.Min(last, lines.Count); line++)
        {
            var candidate = lines[line - 1];
            if (!candidate.IsCommitted)
                return candidate;   // an edit not committed yet is the latest there is
            if (latest is null || candidate.Time > latest.Time)
                latest = candidate;
        }
        return latest;
    }

    /// <summary>"Kieron, 28 days ago"; "Not committed yet" for unsaved or uncommitted edits.</summary>
    public static string Describe(GitBlameLine change, DateTimeOffset now)
        => change.IsCommitted ? $"{change.Author}, {Ago(change.Time, now)}" : "Not committed yet";

    /// <summary>"just now", "5 minutes ago", "3 hours ago", "yesterday", "28 days ago", "4 months ago", "2 years ago".</summary>
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var span = now - then;
        if (span < TimeSpan.FromMinutes(1))
            return "just now";
        if (span < TimeSpan.FromHours(1))
            return Plural((int)span.TotalMinutes, "minute");
        if (span < TimeSpan.FromDays(1))
            return Plural((int)span.TotalHours, "hour");
        if (span < TimeSpan.FromDays(2))
            return "yesterday";
        if (span < TimeSpan.FromDays(60))
            return Plural((int)span.TotalDays, "day");
        if (span < TimeSpan.FromDays(730))
            return Plural((int)(span.TotalDays / 30.4), "month");
        return Plural((int)(span.TotalDays / 365.25), "year");
    }

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";

    private static bool IsHex(string text)
    {
        foreach (var c in text)
            if (!char.IsAsciiHexDigit(c))
                return false;
        return true;
    }
}
