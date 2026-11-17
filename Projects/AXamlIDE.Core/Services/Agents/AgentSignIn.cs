using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Agents;

/// <summary>
/// Runs one of the agent's sign-in commands (<see cref="AcpAuthMethod"/>) out of sight, the way VS Code's Claude
/// sign-in works: the command opens the browser itself, the user signs in there, the browser hands the result back to
/// the command on localhost, and the command exits 0 having saved the login (for Claude Code, in ~/.claude).
///
/// If the browser can't open, or can't get back to localhost, the command also prints a link to open by hand
/// ("If the browser didn't open, visit: https://..."): that page shows a code, which goes back through
/// <see cref="SubmitCode"/> (the command is waiting for it on stdin).
/// </summary>
public sealed class AgentSignIn : IDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _recentOutput = new();
    private bool _disposed;

    private AgentSignIn(Process process)
    {
        _process = process;
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => OnOutput(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
        process.Exited += (_, _) => _done.TrySetResult(!_disposed && SafeExitCode() == 0);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (process.HasExited)
            _done.TrySetResult(SafeExitCode() == 0);
    }

    /// <summary>Start the sign-in: the browser opens soon after. Throws if the command won't start.</summary>
    public static AgentSignIn Start(AcpAuthMethod method)
    {
        var startInfo = new ProcessStartInfo(method.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in method.Args)
            startInfo.ArgumentList.Add(arg);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Couldn't start '{method.Command}'.");
        return new AgentSignIn(process);
    }

    /// <summary>True when the command finished and signed in; false when it failed or was cancelled.</summary>
    public Task<bool> Completion => _done.Task;

    /// <summary>The page to open by hand if the browser didn't open; null until the command prints it. Background thread.</summary>
    public event Action<string>? LinkFound;

    /// <summary>The command's last few lines, to say why it failed.</summary>
    public string RecentOutput { get { lock (_recentOutput) return string.Join(" ", _recentOutput); } }

    /// <summary>The code from the sign-in page, when it was opened by hand.</summary>
    public void SubmitCode(string code)
    {
        try
        {
            _process.StandardInput.WriteLine(code.Trim());
            _process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ObjectDisposedException)
        {
            // It has already finished: Completion says how.
        }
    }

    private void OnOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lock (_recentOutput)
        {
            _recentOutput.Enqueue(line.Trim());
            while (_recentOutput.Count > 4)
                _recentOutput.Dequeue();
        }
        if (FindLink(line) is { } link)
            LinkFound?.Invoke(link);
    }

    /// <summary>The https link on a line of the command's output, or null.</summary>
    internal static string? FindLink(string line)
    {
        var start = line.IndexOf("https://", StringComparison.Ordinal);
        if (start < 0)
            return null;
        var end = line.IndexOfAny([' ', '\t', '"', '\''], start);
        return end < 0 ? line[start..] : line[start..end];
    }

    private int SafeExitCode()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    /// <summary>Cancel: stop the command if it's still waiting.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _done.TrySetResult(false);
        _process.Dispose();
    }
}
