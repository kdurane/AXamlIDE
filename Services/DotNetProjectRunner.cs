using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace AXamlIDE.Services;

public sealed class DotNetProjectRunner : IDisposable
{
    private volatile Process? _activeProcess;

    public event Action<string, bool>? OutputReceived;

    public Task<int> BuildAsync(string projectPath)
        => ExecuteAsync("build", projectPath, isRun: false);

    public Task<int> RunAsync(string projectPath)
        => ExecuteAsync("run", projectPath, isRun: true);

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

    private async Task<int> ExecuteAsync(string verb, string projectPath, bool isRun)
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
        process.StartInfo.ArgumentList.Add("Debug");
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
