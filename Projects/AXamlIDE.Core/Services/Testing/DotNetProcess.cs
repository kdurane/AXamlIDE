using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Testing;

/// <summary>Runs one <c>dotnet</c> command, passing each line of its output on as it comes.</summary>
internal static class DotNetProcess
{
    /// <summary>The exit code. Cancelling stops the process (and what it started), then throws.</summary>
    public static async Task<int> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        Action<string> onLine,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        info.Environment["DOTNET_NOLOGO"] = "1";

        using var process = new Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException("Could not start the dotnet process.");

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
