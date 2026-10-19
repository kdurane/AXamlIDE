using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.NuGet;

/// <summary>
/// The parts of NuGet that belong to the SDK, through the dotnet CLI: the configured sources (every nuget.config
/// that applies), the global packages folder, what a project references, and adding / removing packages (which
/// edits the project the standard way, honours Directory.Packages.props, and restores).
/// </summary>
public static partial class NuGetCli
{
    /// <summary>The sources dotnet would use for a project in <paramref name="directory"/>.</summary>
    public static async Task<IReadOnlyList<NuGetSource>> ListSourcesAsync(string directory, CancellationToken ct)
    {
        var (exitCode, output) = await RunAsync(directory, ct, "nuget", "list", "source", "--format", "detailed");
        return exitCode == 0 ? ParseSources(output) : [];
    }

    /// <summary>~/.nuget/packages (or wherever NUGET_PACKAGES / config puts it), or null if dotnet won't say.</summary>
    public static async Task<string?> GlobalPackagesFolderAsync(string directory, CancellationToken ct)
    {
        var (exitCode, output) = await RunAsync(directory, ct, "nuget", "locals", "global-packages", "--list");
        if (exitCode != 0)
            return null;
        var line = output.Split('\n').FirstOrDefault(l => l.StartsWith("global-packages:", StringComparison.Ordinal));
        return line?["global-packages:".Length..]?.Trim();
    }

    /// <summary>The packages the project references directly, with the version restore resolved.</summary>
    public static async Task<InstalledPackages> ListInstalledAsync(string projectPath, CancellationToken ct)
    {
        var (_, output) = await RunAsync(Path.GetDirectoryName(projectPath)!, ct, "list", projectPath, "package", "--format", "json");
        return ParseInstalled(output);
    }

    public static async Task<NuGetChangeResult> AddAsync(string projectPath, string id, string version, CancellationToken ct)
    {
        var (exitCode, output) = await RunAsync(Path.GetDirectoryName(projectPath)!, ct, "add", projectPath, "package", id, "--version", version);
        return new NuGetChangeResult(exitCode == 0, output);
    }

    public static async Task<NuGetChangeResult> RemoveAsync(string projectPath, string id, CancellationToken ct)
    {
        var (exitCode, output) = await RunAsync(Path.GetDirectoryName(projectPath)!, ct, "remove", projectPath, "package", id);
        return new NuGetChangeResult(exitCode == 0, output);
    }

    public static async Task<NuGetChangeResult> RestoreAsync(string projectPath, CancellationToken ct)
    {
        var (exitCode, output) = await RunAsync(Path.GetDirectoryName(projectPath)!, ct, "restore", projectPath);
        return new NuGetChangeResult(exitCode == 0, output);
    }

    // ---------------------------------------------------------------- parsing (tested)

    /// <summary>
    /// <c>dotnet nuget list source --format detailed</c>:
    /// <code>
    /// Registered Sources:
    ///   1.  nuget.org [Enabled]
    ///       https://api.nuget.org/v3/index.json
    /// </code>
    /// </summary>
    public static IReadOnlyList<NuGetSource> ParseSources(string output)
    {
        var sources = new List<NuGetSource>();
        var lines = output.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (SourceHeader().Match(lines[i]) is not { Success: true } header)
                continue;
            var url = lines[i + 1].Trim();
            if (url.Length == 0)
                continue;
            sources.Add(new NuGetSource(header.Groups["name"].Value.Trim(), url,
                header.Groups["state"].Value.Equals("Enabled", StringComparison.OrdinalIgnoreCase)));
            i++;
        }
        return sources;
    }

    /// <summary><c>dotnet list package --format json</c>: top-level packages merged across target frameworks.</summary>
    public static InstalledPackages ParseInstalled(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0)
            return new InstalledPackages([], FirstLine(output) ?? "dotnet list package gave no output.");

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(output[start..]);
        }
        catch (JsonException)
        {
            return new InstalledPackages([], FirstLine(output));
        }

        using (json)
        {
            var problems = json.RootElement.TryGetProperty("problems", out var list) && list.ValueKind == JsonValueKind.Array
                ? string.Join(" ", list.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : null))
                : null;

            var byId = new Dictionary<string, (string? Requested, string? Resolved, List<string> Frameworks)>(StringComparer.OrdinalIgnoreCase);
            if (json.RootElement.TryGetProperty("projects", out var projects))
                foreach (var project in projects.EnumerateArray())
                {
                    if (!project.TryGetProperty("frameworks", out var frameworks))
                        continue;
                    foreach (var framework in frameworks.EnumerateArray())
                    {
                        var name = framework.TryGetProperty("framework", out var f) ? f.GetString() ?? "" : "";
                        if (!framework.TryGetProperty("topLevelPackages", out var packages))
                            continue;
                        foreach (var package in packages.EnumerateArray())
                        {
                            if (package.TryGetProperty("autoReferenced", out var auto) && auto.ValueKind == JsonValueKind.True)
                                continue;   // implicit (SDK) references can't be managed
                            var id = package.GetProperty("id").GetString() ?? "";
                            var requested = package.TryGetProperty("requestedVersion", out var r) ? r.GetString() : null;
                            var resolved = package.TryGetProperty("resolvedVersion", out var v) ? v.GetString() : null;
                            if (!byId.TryGetValue(id, out var entry))
                                byId[id] = entry = (requested, resolved, []);
                            entry.Frameworks.Add(name);
                        }
                    }
                }

            var installed = byId.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new InstalledPackage(pair.Key, pair.Value.Requested, pair.Value.Resolved, pair.Value.Frameworks))
                .ToList();
            return new InstalledPackages(installed, string.IsNullOrWhiteSpace(problems) ? null : problems);
        }
    }

    private static string? FirstLine(string text)
        => text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);

    [GeneratedRegex(@"^\s*\d+\.\s+(?<name>.+?)\s+\[(?<state>Enabled|Disabled)\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceHeader();

    // ---------------------------------------------------------------- running dotnet

    private static async Task<(int ExitCode, string Output)> RunAsync(string directory, CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";   // the parsers read English output
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        using var cancel = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
        });
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var error = await stderr;
        var output = await stdout;
        return (process.ExitCode, error.Length == 0 ? output : output + error);
    }
}
