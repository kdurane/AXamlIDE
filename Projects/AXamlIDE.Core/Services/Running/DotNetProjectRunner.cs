using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Running;

public sealed class DotNetProjectRunner : IDisposable
{
    private volatile Process? _activeProcess;

    public event Action<string, bool>? OutputReceived;

    /// <param name="configuration">"Debug" or "Release" (dotnet's <c>--configuration</c>).</param>
    public Task<int> BuildAsync(string projectPath, string configuration)
        => ExecuteAsync("build", projectPath, configuration, isRun: false);

    /// <summary>Runs the already built project (<c>--no-build</c>); build it first with the same configuration.</summary>
    public Task<int> RunAsync(string projectPath, string configuration)
        => ExecuteAsync("run", projectPath, configuration, isRun: true);

    public void Stop()
    {
        var process = _activeProcess;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Debug.WriteLine($"Could not stop dotnet process: {ex}");
        }
    }

    private async Task<int> ExecuteAsync(string verb, string projectPath, string configuration, bool isRun)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(projectPath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(verb);
        process.StartInfo.ArgumentList.Add(projectPath);
        process.StartInfo.ArgumentList.Add("--configuration");
        process.StartInfo.ArgumentList.Add(configuration);
        if (isRun)
            process.StartInfo.ArgumentList.Add("--no-build");

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start the dotnet process.");

            _activeProcess = process;
            var standardOutput = ReadOutputAsync(process.StandardOutput, isRun);
            var standardError = ReadOutputAsync(process.StandardError, isRun);
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            return process.ExitCode;
        }
        finally
        {
            if (ReferenceEquals(_activeProcess, process))
                _activeProcess = null;
            process.Dispose();
        }
    }

    private async Task ReadOutputAsync(StreamReader reader, bool isRun)
    {
        while (await reader.ReadLineAsync() is { } line)
            OutputReceived?.Invoke(line, isRun);
    }

    public void Dispose() => Stop();
}
