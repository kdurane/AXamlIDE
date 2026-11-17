using System;
using System.Collections.Generic;
using System.IO;

namespace AXamlIDE.Services.Terminal;

/// <summary>A shell to run in the Terminal panel: the program, its arguments, and the name the panel shows.</summary>
public sealed record ShellProgram(string Path, IReadOnlyList<string> Arguments, string Name);

/// <summary>
/// Which shell the Terminal panel runs, as VS Code picks its default: the user's login shell ($SHELL) on Linux and
/// macOS, PowerShell on Windows (PowerShell 7 when it's installed, then Windows PowerShell, then cmd).
/// </summary>
public static class ShellLocator
{
    public static ShellProgram Find()
        => OperatingSystem.IsWindows() ? FindWindowsShell() : FindUnixShell();

    private static ShellProgram FindWindowsShell()
    {
        if (FindOnPath("pwsh.exe") is { } pwsh)
            return new ShellProgram(pwsh, ["-NoLogo"], "pwsh");
        if (FindOnPath("powershell.exe") is { } powerShell)
            return new ShellProgram(powerShell, ["-NoLogo"], "powershell");
        var cmd = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe";
        return new ShellProgram(cmd, [], "cmd");
    }

    private static ShellProgram FindUnixShell()
    {
        var path = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } userShell && File.Exists(userShell)
            ? userShell
            : File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        // macOS terminals start a login shell (it reads ~/.zprofile, where Homebrew puts itself on the PATH);
        // Linux ones start an interactive shell that reads ~/.bashrc. VS Code does the same.
        IReadOnlyList<string> arguments = OperatingSystem.IsMacOS() ? ["-l"] : [];
        return new ShellProgram(path, arguments, System.IO.Path.GetFileName(path));
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            if (folder.Length == 0)
                continue;
            var candidate = System.IO.Path.Combine(folder, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
