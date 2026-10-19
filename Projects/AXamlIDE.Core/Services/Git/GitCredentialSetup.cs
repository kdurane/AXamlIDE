using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Git;

/// <summary>
/// Sets up Git Credential Manager (GCM), so pushing to and pulling from HTTPS remotes such as GitHub signs in
/// through the browser (GCM's window, then GitHub's "authorise" page) and remembers the token in the system
/// keyring, as Visual Studio does. Git runs without a terminal here, so without a credential helper it can't
/// ask for a password at all.
///
///   1. Install GCM if it isn't there: <c>dotnet tool install -g git-credential-manager</c>. (On Windows it comes
///      with Git for Windows, so it's only reported missing.)
///   2. <c>git-credential-manager configure</c>: makes it git's credential helper (global git config).
///   3. On Linux, if no store is chosen: <c>credential.credentialStore secretservice</c> (the desktop keyring),
///      or <c>cache</c> (memory, for a while) when there's no desktop session.
///
/// Only <see cref="SetUpAsync"/> changes anything, and the IDE asks first.
/// </summary>
public sealed class GitCredentialSetup
{
    public const string ToolName = "git-credential-manager";

    /// <summary>The credential helper git uses in <paramref name="directory"/> (any config level), or null if none.</summary>
    public async Task<string?> GetHelperAsync(string directory)
    {
        var (exitCode, output, _) = await RunAsync("git", ["config", "--get", "credential.helper"], directory);
        return exitCode == 0 && output.Trim().Length > 0 ? output.Trim() : null;
    }

    /// <summary>GCM's executable: on PATH, or where <c>dotnet tool install -g</c> puts it. Null if not installed.</summary>
    public static string? FindManager()
    {
        var name = OperatingSystem.IsWindows() ? ToolName + ".exe" : ToolName;
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(DotnetToolsFolder());
        return folders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }

    /// <summary>Install (if needed) and configure GCM. Returns null when it worked, or what went wrong.</summary>
    public async Task<string?> SetUpAsync(IProgress<string>? progress = null)
    {
        var manager = FindManager();
        if (manager is null)
        {
            if (OperatingSystem.IsWindows())
                return "Git Credential Manager isn't installed. It comes with Git for Windows: reinstall Git with it ticked.";

            progress?.Report("Installing Git Credential Manager (dotnet tool install -g git-credential-manager)...");
            var (installed, _, installError) = await RunAsync("dotnet", ["tool", "install", "-g", ToolName], null);
            manager = FindManager();
            if (installed != 0 && manager is null)
                return "Couldn't install Git Credential Manager: " + FirstLine(installError);
            if (manager is null)
                return "Git Credential Manager was installed but can't be found (expected in " + DotnetToolsFolder() + ").";
        }

        progress?.Report("Making Git Credential Manager git's credential helper...");
        var (configured, _, configureError) = await RunAsync(manager, ["configure"], null);
        if (configured != 0)
            return "Couldn't configure Git Credential Manager: " + FirstLine(configureError);

        if (OperatingSystem.IsLinux())
        {
            var (hasStore, _, _) = await RunAsync("git", ["config", "--global", "--get", "credential.credentialStore"], null);
            if (hasStore != 0)
            {
                var store = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")) ? "cache" : "secretservice";
                await RunAsync("git", ["config", "--global", "credential.credentialStore", store], null);
            }
        }

        return null;
    }

    private static string DotnetToolsFolder()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown error";

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string file, string[] arguments, string? directory)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        try
        {
            using var process = Process.Start(startInfo)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output, await error);
        }
        catch (Win32Exception ex)
        {
            return (-1, "", $"{file} couldn't be started ({ex.Message}).");
        }
    }
}
