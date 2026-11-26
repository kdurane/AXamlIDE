using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AXamlIDE.Services.Debugging;

/// <summary>Settings > Debugging > Debugger.</summary>
public enum DebuggerChoice
{
    /// <summary>SharpDbg when it can run (a final .NET 11 runtime is installed), otherwise netcoredbg.</summary>
    Automatic,
    SharpDbg,
    NetCoreDbg
}

/// <summary>
/// A debug adapter (a program speaking the Debug Adapter Protocol over stdin/stdout) and how to start it.
/// </summary>
/// <param name="Name">"SharpDbg" or "netcoredbg", for messages.</param>
/// <param name="FileName">The program to start.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="RunsInTerminal">It can ask the IDE to start the program in a terminal (DAP's runInTerminal), so the
/// program gets a real console: input, colours, cursor movement. netcoredbg can't, so its program's output comes back
/// through the adapter and it can't read input.</param>
/// <param name="FormatsValues">It shows values as Visual Studio does ([DebuggerDisplay], type proxies, Count, ToString,
/// Raw View), so the IDE takes them as they come rather than working them out (see IDebugEvaluator.FormatsValues).</param>
/// <param name="DecompilesExternalCode">With Just My Code off it decompiles code that has no PDB when it reaches it,
/// and answers SharpDbg's resolveStackFrame request (decompile one Call Stack frame): see DecompiledSources.</param>
public sealed record DebugAdapter(
    string Name,
    string FileName,
    IReadOnlyList<string> Arguments,
    bool RunsInTerminal,
    bool FormatsValues,
    bool DecompilesExternalCode = false);

/// <summary>
/// The debuggers that come with the IDE (copied next to it at build time), and which one to use:
///  - SharpDbg (sharpdbg/, MIT, written in C#): DebuggerDisplay and type proxies, LINQ and interpolation in watches,
///    runs the program in the IDE's terminal. Needs a .NET 11 runtime (a final release, not a preview).
///  - netcoredbg (netcoredbg/, Samsung, MIT): runs anywhere; what Automatic falls back to without .NET 11.
/// </summary>
public static class DebugAdapters
{
    /// <summary>
    /// The debugger for <paramref name="choice"/>, and a note when it isn't what was asked for (SharpDbg chosen but no
    /// .NET 11 runtime): the status bar shows it.
    /// </summary>
    public static (DebugAdapter Adapter, string? Note) Choose(DebuggerChoice choice)
    {
        if (choice is DebuggerChoice.SharpDbg or DebuggerChoice.Automatic)
        {
            if (SharpDbg() is { } sharpDbg)
                return (sharpDbg, null);
            if (choice == DebuggerChoice.SharpDbg)
                return (NetCoreDbg(), $"SharpDbg needs a .NET 11 runtime ({SharpDbgUnavailableReason()}), so netcoredbg is debugging instead.");
        }
        return (NetCoreDbg(), null);
    }

    /// <summary>What Settings says about a choice: which debugger it means on this machine, and why.</summary>
    public static string Describe(DebuggerChoice choice)
    {
        var sharpDbg = SharpDbg() is not null;
        return choice switch
        {
            DebuggerChoice.NetCoreDbg => "netcoredbg: works everywhere, but a console program can't read input while debugged.",
            _ when sharpDbg => "SharpDbg is used: the program runs in its own terminal, and you get DebuggerDisplay and LINQ in watches.",
            DebuggerChoice.SharpDbg => $"SharpDbg needs a .NET 11 runtime ({SharpDbgUnavailableReason()}), so netcoredbg is used for now.",
            _ => $"netcoredbg is used for now: SharpDbg needs a .NET 11 runtime ({SharpDbgUnavailableReason()}). It's picked as soon as one is installed."
        };
    }

    // ------------------------------------------------------------- SharpDbg

    /// <summary>The runtime SharpDbg is built for (its runtimeconfig asks for this or later).</summary>
    private static readonly Version SharpDbgRuntime = new(11, 0, 0);

    /// <summary>
    /// For trying SharpDbg on a preview runtime: AXAMLIDE_SHARPDBG_FX=11.0.0-rc.3.26522.148 starts it on exactly that
    /// runtime (dotnet exec --fx-version). Not a setting: previews aren't for everyday use.
    /// </summary>
    private static string? PreviewRuntimeOverride => Environment.GetEnvironmentVariable("AXAMLIDE_SHARPDBG_FX") is { Length: > 0 } fx ? fx : null;

    public static string SharpDbgPath => Path.Combine(AppContext.BaseDirectory, "sharpdbg", "SharpDbg.Cli.dll");

    /// <summary>SharpDbg, if it's here and a runtime it can run on is installed; else null.</summary>
    public static DebugAdapter? SharpDbg()
    {
        if (!File.Exists(SharpDbgPath) || DotNetRuntimes.DotNetPath() is not { } dotnet)
            return null;

        List<string> arguments = [];
        if (PreviewRuntimeOverride is { } fx)
            arguments.AddRange(["exec", "--fx-version", fx]);
        else if (!DotNetRuntimes.HasReleased(SharpDbgRuntime))
            return null;
        arguments.AddRange([SharpDbgPath, "--interpreter=vscode"]);
        return new DebugAdapter("SharpDbg", dotnet, arguments, RunsInTerminal: true, FormatsValues: true, DecompilesExternalCode: true);
    }

    private static string SharpDbgUnavailableReason()
    {
        if (!File.Exists(SharpDbgPath))
            return "and it isn't installed with the IDE";
        var preview = DotNetRuntimes.Installed().Where(version => version.Major >= SharpDbgRuntime.Major && version.IsPreview).ToList();
        return preview.Count > 0
            ? $"a final release: only the preview {preview[^1].Text} is installed"
            : "none is installed";
    }

    // ------------------------------------------------------------- netcoredbg

    public static DebugAdapter NetCoreDbg() => new(
        "netcoredbg",
        NetCoreDbgPath(),
        ["--interpreter=vscode", "--engineLogging=" + Path.Combine(Path.GetTempPath(), "netcoredbg.log")],
        RunsInTerminal: false,
        FormatsValues: false);

    private static string NetCoreDbgPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("AXAMLIDE_NETCOREDBG_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath))
                return overridePath;
            throw new FileNotFoundException(
                $"AXAMLIDE_NETCOREDBG_PATH points to a missing file: '{overridePath}'.", overridePath);
        }

        var executable = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";

        // Copied next to the app at build time from Projects/netcoredbg (see AXamlIDE.csproj).
        var bundled = Path.Combine(AppContext.BaseDirectory, "netcoredbg", executable);
        if (File.Exists(bundled))
            return bundled;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "netcoredbg was not found next to the IDE or on PATH. Install the netcoredbg standalone executable or set AXAMLIDE_NETCOREDBG_PATH.");
    }
}

/// <summary>The .NET runtimes installed for the <c>dotnet</c> on PATH (read from its shared/Microsoft.NETCore.App folder).</summary>
public static class DotNetRuntimes
{
    /// <summary>A runtime version as its folder names it ("10.0.13", "11.0.0-rc.3.26522.148").</summary>
    public sealed record RuntimeVersion(string Text, Version Number, bool IsPreview)
    {
        public int Major => Number.Major;

        public static RuntimeVersion? Parse(string text)
        {
            var dash = text.IndexOf('-');
            return Version.TryParse(dash < 0 ? text : text[..dash], out var number)
                ? new RuntimeVersion(text, number, IsPreview: dash >= 0)
                : null;
        }
    }

    /// <summary>The dotnet executable: DOTNET_ROOT's, else the first on PATH (links followed). Null if there's none.</summary>
    public static string? DotNetPath()
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var folders = new List<string>();
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            folders.Add(root);
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var folder in folders)
        {
            var candidate = Path.Combine(folder, executable);
            if (File.Exists(candidate))
                return new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
        }
        return null;
    }

    /// <summary>Every installed Microsoft.NETCore.App version, oldest first.</summary>
    public static IReadOnlyList<RuntimeVersion> Installed()
    {
        if (DotNetPath() is not { } dotnet || Path.GetDirectoryName(dotnet) is not { } root)
            return [];
        var shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(shared))
            return [];
        return
        [
            .. Directory.GetDirectories(shared)
                .Select(folder => RuntimeVersion.Parse(Path.GetFileName(folder)))
                .OfType<RuntimeVersion>()
                .OrderBy(version => version.Number)
        ];
    }

    /// <summary>A final (not preview) runtime of at least <paramref name="minimum"/> is installed.</summary>
    public static bool HasReleased(Version minimum) => Installed().Any(version => !version.IsPreview && version.Number >= minimum);
}

/// <summary>A program the debugger asked to start in a terminal (DAP's runInTerminal arguments).</summary>
/// <param name="Folder">Its working folder.</param>
/// <param name="CommandLine">The program and its arguments.</param>
/// <param name="Environment">Variables to set (a null value removes one).</param>
public sealed record TerminalLaunch(string Folder, IReadOnlyList<string> CommandLine, IReadOnlyDictionary<string, string?> Environment, string? Title)
{
    public static TerminalLaunch? From(System.Text.Json.JsonElement arguments)
    {
        if (!arguments.TryGetProperty("args", out var args) || args.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;
        var commandLine = args.EnumerateArray().Select(arg => arg.GetString() ?? "").ToList();
        if (commandLine.Count == 0)
            return null;
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (arguments.TryGetProperty("env", out var env) && env.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var variable in env.EnumerateObject())
                environment[variable.Name] = variable.Value.ValueKind == System.Text.Json.JsonValueKind.Null ? null : variable.Value.ToString();
        var folder = arguments.TryGetProperty("cwd", out var cwd) ? cwd.GetString() ?? "" : "";
        var title = arguments.TryGetProperty("title", out var t) ? t.GetString() : null;
        return new TerminalLaunch(folder, commandLine, environment, title);
    }
}
