using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Processes;

/// <summary>
/// Runs a command-line tool (dotnet, git), passing each line of its output and errors on as it comes, for a log or a
/// progress line. Progress that redraws itself with \r ("Receiving objects: 45%") arrives as a line per update.
/// </summary>
public static class LineProcess
{
    /// <summary>
    /// The exit code. Cancelling stops the process (and what it started), then throws. Throws
    /// <see cref="Win32Exception"/> when the tool isn't installed.
    /// </summary>
    public static async Task<int> RunAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        Action<string> onLine,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // never wait for input (a prompt would hang)
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            info.Environment[name] = value;

        using var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.Close();

        await using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        var stdout = ReadAsync(process.StandardOutput, onLine);
        var stderr = ReadAsync(process.StandardError, onLine);
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static async Task ReadAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
            onLine(line);
    }
}
