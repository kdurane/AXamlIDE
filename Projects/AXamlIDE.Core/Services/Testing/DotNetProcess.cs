using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Processes;

namespace AXamlIDE.Services.Testing;

/// <summary>Runs one <c>dotnet</c> command, passing each line of its output on as it comes.</summary>
internal static class DotNetProcess
{
    private static readonly Dictionary<string, string> Environment = new() { ["DOTNET_NOLOGO"] = "1" };

    /// <summary>The exit code. Cancelling stops the process (and what it started), then throws.</summary>
    public static Task<int> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken ct)
        => LineProcess.RunAsync("dotnet", workingDirectory, arguments, onLine, ct, Environment);
}
