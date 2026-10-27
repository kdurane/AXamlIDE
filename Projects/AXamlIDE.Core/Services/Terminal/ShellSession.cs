using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Terminal;

/// <summary>
/// The shell behind the Terminal panel: the user's shell on Linux and macOS ($SHELL when it's bash, zsh or sh, else
/// bash), PowerShell on Windows. Commands are typed into its standard input.
///
/// It runs on pipes, not a pseudo-terminal. Everyday commands work (dotnet, git, ls, cd, environment variables
/// that stick between commands), but full-screen programs (vim, top, less) and password prompts need a real
/// terminal and won't. Colour codes are removed from the output.
///
/// After each command the shell prints a marker with the exit code and its working directory
/// (<see cref="ShellOutputSplitter"/>), which is how <see cref="CommandFinished"/> knows a command has ended and
/// where <c>cd</c> went. Events are raised on background threads.
/// </summary>
public sealed class ShellSession : IDisposable
{
    private readonly Process _process;
    private readonly bool _isPowerShell;
    private readonly ShellOutputSplitter _splitter = new();
    private readonly AnsiStripper _stdoutAnsi = new();
    private readonly AnsiStripper _stderrAnsi = new();
    private volatile bool _busy;
    private bool _disposed;

    private ShellSession(Process process, string name, bool isPowerShell, string workingDirectory)
    {
        _process = process;
        _isPowerShell = isPowerShell;
        Name = name;
        WorkingDirectory = workingDirectory;
    }

    /// <summary>Output to show: the text (not always whole lines) and whether it came from standard error.</summary>
    public event Action<string, bool>? OutputReceived;

    /// <summary>A command ended, with its exit code. <see cref="WorkingDirectory"/> is already updated.</summary>
    public event Action<int>? CommandFinished;

    /// <summary>The shell has exited (the user typed <c>exit</c>, or it was stopped).</summary>
    public event Action? Exited;

    /// <summary>The shell's name, for the panel: "bash", "zsh", "pwsh"...</summary>
    public string Name { get; }

    /// <summary>Where the shell is, as of the last command that ended.</summary>
    public string WorkingDirectory { get; private set; }

    /// <summary>A command is running (typed lines go to it as input).</summary>
    public bool IsBusy => _busy;

    /// <summary>
    /// On Windows there's no way to send Ctrl+C to a program without a console, so <see cref="Interrupt"/> stops
    /// the whole shell; the panel starts a new one in the same folder.
    /// </summary>
    public bool InterruptStopsShell => OperatingSystem.IsWindows();

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Start a shell in <paramref name="workingDirectory"/>.</summary>
    /// <exception cref="InvalidOperationException">No shell could be started.</exception>
    public static ShellSession Start(string workingDirectory)
    {
        var (path, isPowerShell) = FindShell();
        var info = new ProcessStartInfo(path)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Tell programs this isn't a full terminal, so they don't draw progress bars and menus.
        info.Environment["TERM"] = "dumb";
        if (isPowerShell)
            foreach (var argument in new[] { "-NoLogo", "-NonInteractive", "-Command", "-" })
                info.ArgumentList.Add(argument);   // read commands from standard input

        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Could not start {path}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start {path}: {ex.Message}", ex);
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var session = new ShellSession(process, name, isPowerShell, workingDirectory);
        session.StartReading();
        return session;
    }

    /// <summary>Run a command line (as if typed at the prompt and Enter pressed).</summary>
    public void Run(string commandLine)
    {
        _busy = true;
        Write(WithMarker(commandLine.Trim()));
    }

    /// <summary>Send a line to the running command's standard input (a program asking a question).</summary>
    public void WriteInput(string line) => Write(line);

    /// <summary>
    /// Ctrl+C: interrupt what's running. On Linux and macOS the shell's child processes get SIGINT, as in a terminal.
    /// If nothing was running (a command that never printed its marker, e.g. one ending in a # comment), the shell is
    /// asked for a fresh marker so the panel stops waiting. On Windows see <see cref="InterruptStopsShell"/>.
    /// </summary>
    public void Interrupt()
    {
        if (InterruptStopsShell)
        {
            Stop();
            return;
        }

        if (!SignalChildren())
            Write(Marker());
    }

    /// <summary>Stop the shell and everything it started.</summary>
    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"Could not stop the shell: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _process.Dispose();
    }

    // ------------------------------------------------------------- the command line

    /// <summary>The command, then the marker printed when it ends (on the same line, so a program reading input
    /// doesn't read the marker command as its input).</summary>
    private string WithMarker(string commandLine)
    {
        if (commandLine.Length == 0)
            return Marker();
        // "sleep 5 &" can't be followed by ";": "&" already separates the two.
        var separator = commandLine.EndsWith('&') && !commandLine.EndsWith("&&", StringComparison.Ordinal) ? " " : "; ";
        return commandLine + separator + Marker();
    }

    /// <summary>The command that prints the marker: exit code and working directory (see ShellOutputSplitter).</summary>
    private string Marker() => _isPowerShell
        ? "\"$([char]30)$(if ($?) { 0 } elseif ($LASTEXITCODE) { $LASTEXITCODE } else { 1 })$([char]31)$($PWD.Path)$([char]30)\""
        : "printf '\\036%s\\037%s\\036\\n' \"$?\" \"$PWD\"";

    private void Write(string line)
    {
        try
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _busy = false;   // the shell has gone; Exited says so
        }
    }

    /// <summary>SIGINT to the shell's child processes; false when it had none (or pkill isn't there).</summary>
    private bool SignalChildren()
    {
        try
        {
            using var pkill = Process.Start(new ProcessStartInfo("pkill")
            {
                ArgumentList = { "-INT", "-P", _process.Id.ToString() },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (pkill is null)
                return false;
            pkill.WaitForExit(2000);
            return pkill.HasExited && pkill.ExitCode == 0;   // 1 = no process matched
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------- output

    private void StartReading()
    {
        var stdout = Task.Run(() => ReadAsync(_process.StandardOutput, isError: false));
        _ = Task.Run(() => ReadAsync(_process.StandardError, isError: true));
        _ = stdout.ContinueWith(async _ =>
        {
            try
            {
                await _process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }

            _busy = false;
            Exited?.Invoke();
        }, TaskScheduler.Default);
    }

    private async Task ReadAsync(StreamReader reader, bool isError)
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
            {
                var chunk = new string(buffer, 0, count);
                if (isError)
                {
                    var text = _stderrAnsi.Strip(chunk);
                    if (text.Length > 0)
                        OutputReceived?.Invoke(text, true);
                }
                else
                {
                    _splitter.Feed(_stdoutAnsi.Strip(chunk), OnText, OnCommandEnded);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The shell went away mid-read; Exited follows.
        }
    }

    private void OnText(string text) => OutputReceived?.Invoke(text, false);

    private void OnCommandEnded(int exitCode, string directory)
    {
        if (directory.Length > 0)
            WorkingDirectory = directory;
        _busy = false;
        CommandFinished?.Invoke(exitCode);
    }

    // ------------------------------------------------------------- which shell

    private static (string Path, bool IsPowerShell) FindShell()
    {
        if (OperatingSystem.IsWindows())
        {
            var powerShell = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe")
                             ?? throw new InvalidOperationException("PowerShell wasn't found on the PATH.");
            return (powerShell, true);
        }

        // The user's shell when it understands the marker command (POSIX shells do; fish doesn't).
        var userShell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(userShell) && File.Exists(userShell)
            && Path.GetFileName(userShell) is "bash" or "zsh" or "sh" or "dash" or "ksh")
            return (userShell, false);
        return (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh", false);
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (folder.Length == 0)
                continue;
            var candidate = Path.Combine(folder, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
