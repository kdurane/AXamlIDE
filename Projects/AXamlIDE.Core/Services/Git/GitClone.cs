using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Processes;

namespace AXamlIDE.Services.Git;

/// <summary>Clone a Git repository (the start window's "Clone a repository").</summary>
public static class GitClone
{
    private static readonly Dictionary<string, string> Environment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",   // no hidden username prompt: credentials come from a helper or the URL
        ["LC_ALL"] = "C"
    };

    /// <summary>
    /// The folder name a clone of <paramref name="url"/> gets, as git picks it: the last part of the path without
    /// ".git" ("https://github.com/AvaloniaUI/Avalonia.git" and "git@github.com:AvaloniaUI/Avalonia" give "Avalonia").
    /// Empty when there's no name in it yet.
    /// </summary>
    public static string RepositoryName(string url)
    {
        var trimmed = url.Trim().TrimEnd('/', '\\');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        var cut = trimmed.LastIndexOfAny(['/', '\\', ':']);
        return cut < 0 || cut == trimmed.Length - 1 ? "" : trimmed[(cut + 1)..];
    }

    /// <summary>
    /// Clone into <paramref name="targetFolder"/> (which must not exist, or be empty), passing git's progress lines
    /// on. Throws <see cref="GitException"/> with git's last message when it fails.
    /// </summary>
    public static async Task CloneAsync(string url, string targetFolder, Action<string> onLine, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(targetFolder))!;
        Directory.CreateDirectory(parent);
        var lines = new List<string>();
        void Collect(string line)
        {
            lock (lines)
                lines.Add(line);
            onLine(line);
        }

        int exitCode;
        try
        {
            exitCode = await LineProcess.RunAsync("git", parent, ["clone", "--progress", url.Trim(), targetFolder], Collect, ct, Environment);
        }
        catch (Win32Exception ex)
        {
            throw new GitException($"Couldn't run git ({ex.Message}). Is git installed?");
        }

        if (exitCode != 0)
        {
            string message;
            lock (lines)
                message = lines.LastOrDefault(line => line.StartsWith("fatal:", StringComparison.Ordinal))
                          ?? lines.LastOrDefault(line => line.Length > 0) ?? $"git clone failed ({exitCode}).";
            throw new GitException(message);
        }
    }

    /// <summary>
    /// What to open after cloning (or creating) into <paramref name="folder"/>: its solution when there's exactly one
    /// at the top, else the folder (Visual Studio opens the folder and lists the solutions in it).
    /// </summary>
    public static string PathToOpen(string folder)
    {
        var solutions = Directory.EnumerateFiles(folder, "*.sln").Concat(Directory.EnumerateFiles(folder, "*.slnx")).Distinct().ToList();
        return solutions.Count == 1 ? solutions[0] : folder;
    }
}
