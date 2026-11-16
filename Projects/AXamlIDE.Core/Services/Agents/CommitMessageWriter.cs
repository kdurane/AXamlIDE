using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Services.Agents;

/// <summary>
/// Writes a commit message for the changes about to be committed (the Git panel's sparkle button, like VS Code's
/// Copilot one). It runs the Claude Code CLI once in print mode (claude -p) with the diff on stdin: no tools, no
/// settings, hooks or project files, nothing saved, so it can only answer with text. It uses the CLI's sign-in,
/// the same as the Claude panel.
/// </summary>
public sealed class CommitMessageWriter(GitService git)
{
    /// <summary>A fast, cheap model: a commit message doesn't need a big one.</summary>
    public const string Model = "haiku";

    /// <summary>Diffs are cut to this many characters, so a huge change (a generated file) can't fill the context.</summary>
    internal const int MaxDiffLength = 60_000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    internal const string SystemPrompt =
        "You write git commit messages. Reply with only the commit message: a subject line of at most 72 characters " +
        "in the same style as the repository's recent commits (if they start with a type such as \"feat:\" or " +
        "\"fix:\", start with the type that fits), then, only if the change needs explaining, a blank " +
        "line and a short body wrapped at 72 characters. Describe what changed and why, not file by file. " +
        "No code fences, quotes, or preamble.";

    /// <summary>
    /// A message for the commit the Git panel would make: the staged changes, or (when nothing is staged) every
    /// change, including <paramref name="newFiles"/> (untracked files Commit All would add, by relative path).
    /// </summary>
    /// <exception cref="InvalidOperationException">The CLI isn't installed, failed, or said nothing.</exception>
    public async Task<string> WriteAsync(string repositoryRoot, bool staged, IReadOnlyList<string> newFiles, CancellationToken ct = default)
    {
        var cli = ClaudeAgentInstall.FindCli()
            ?? throw new InvalidOperationException("Claude isn't installed: open the Claude panel (View > Claude) to download it.");

        var diff = await git.GetDiffAsync(repositoryRoot, staged);
        if (diff.Length == 0 && newFiles.Count == 0)
            throw new InvalidOperationException("There are no changes to describe.");
        var subjects = await git.GetRecentSubjectsAsync(repositoryRoot, 10);

        var reply = await RunCliAsync(cli, repositoryRoot, BuildPrompt(subjects, diff, newFiles), ct);
        return Clean(reply) is { Length: > 0 } message
            ? message
            : throw new InvalidOperationException("Claude didn't return a message.");
    }

    /// <summary>What's sent: the recent subjects (to copy their style), then the diff, then any new files.</summary>
    internal static string BuildPrompt(IReadOnlyList<string> recentSubjects, string diff, IReadOnlyList<string> newFiles)
    {
        var prompt = new StringBuilder();
        if (recentSubjects.Count > 0)
        {
            prompt.AppendLine("Recent commit subjects in this repository (match their style):");
            foreach (var subject in recentSubjects)
                prompt.Append("- ").AppendLine(subject);
            prompt.AppendLine();
        }

        if (diff.Length > 0)
        {
            prompt.AppendLine("The change:");
            if (diff.Length > MaxDiffLength)
                prompt.Append(diff, 0, MaxDiffLength).AppendLine().AppendLine($"[... {diff.Length - MaxDiffLength} more characters of diff left out]");
            else
                prompt.Append(diff);
            prompt.AppendLine();
        }

        if (newFiles.Count > 0)
        {
            prompt.AppendLine("New files being added:");
            foreach (var file in newFiles)
                prompt.Append("- ").AppendLine(file);
        }
        return prompt.ToString();
    }

    /// <summary>The reply without spaces around it, or a code fence the model put round it anyway.</summary>
    internal static string Clean(string reply)
    {
        var text = reply.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = text.IndexOf('\n');
            text = firstLineEnd < 0 ? "" : text[(firstLineEnd + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal))
                text = text[..^3];
            text = text.Trim();
        }
        return text.Replace("\r\n", "\n");
    }

    private static async Task<string> RunCliAsync(string cli, string workingDirectory, string prompt, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(cli)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in (string[])
                 ["-p", "--model", Model, "--tools", "", "--no-session-persistence", "--setting-sources", "",
                  "--disable-slash-commands", "--system-prompt", SystemPrompt])
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Couldn't run Claude ({ex.Message}).");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);   // its warnings (sign-in ending soon...) go here
            await process.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0)
            {
                var message = (await error).Trim().Split('\n').LastOrDefault(line => line.Trim().Length > 0)?.Trim();
                var stdout = (await output).Trim();   // print mode reports some failures ("Not logged in") on stdout
                throw new InvalidOperationException("Claude failed: " + (stdout.Length > 0 ? stdout : message ?? $"exit code {process.ExitCode}"));
            }
            return await output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Claude took too long to answer.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }
}
