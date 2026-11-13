using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace AXamlIDE.Services.Running;

/// <summary>
/// One error or warning from a build, as MSBuild prints it. <see cref="Path"/> is absolute when the line gave one
/// (or could be made one from the project); <see cref="Line"/> is 0 when the message has no location
/// (an MSB error about the project itself).
/// </summary>
/// <param name="Severity">An LspDiagnosticSeverity value (1 = error, 2 = warning).</param>
public sealed record BuildDiagnostic(string Path, int Line, int Column, int Severity, string Code, string Message, string? Project)
{
    public bool IsError => Severity == 1;
    public bool HasLocation => Path.Length > 0 && Line > 0;
}

/// <summary>
/// Reads the errors and warnings out of <c>dotnet build</c> output. MSBuild's canonical format is
/// <c>path(line,col): error CS1002: message [project.csproj]</c>; the column, the code and the project are each
/// optional, and errors with no file at all look like <c>MSBUILD : error MSB1009: message</c>. A build prints
/// each diagnostic twice (once as it happens, again in the summary), so <see cref="Parse"/> returns each once.
/// </summary>
public static partial class BuildOutputParser
{
    // path(line[,col]): error|warning CODE: message [project]
    [GeneratedRegex(@"^\s*(?<path>[^()]+?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)?\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\[\]]+\.\w+)\])?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocatedPattern();

    // origin : error|warning CODE: message [project]   (no line: an MSB error, or NuGet's NU warnings)
    [GeneratedRegex(@"^\s*(?<path>.*?)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)?\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\[\]]+\.\w+)\])?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnlocatedPattern();

    /// <summary>The diagnostic on one output line, or null when the line isn't one.</summary>
    public static BuildDiagnostic? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("error", StringComparison.OrdinalIgnoreCase)
                                                && !line.Contains("warning", StringComparison.OrdinalIgnoreCase))
            return null;

        var match = LocatedPattern().Match(line);
        if (match.Success)
        {
            var project = Group(match, "proj");
            var path = ResolvePath(match.Groups["path"].Value.Trim(), project);
            return new BuildDiagnostic(
                path,
                int.Parse(match.Groups["line"].Value),
                match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value) : 1,
                SeverityOf(match.Groups["sev"].Value),
                match.Groups["code"].Value,
                match.Groups["msg"].Value.Trim(),
                project);
        }

        match = UnlocatedPattern().Match(line);
        if (!match.Success)
            return null;

        var origin = match.Groups["path"].Value.Trim();
        var unlocatedProject = Group(match, "proj");
        // "MSBUILD : error ..." or "CSC : error ...": the tool's name, not a file.
        var filePath = origin.Length > 0 && (origin.Contains('\\') || origin.Contains('/') || origin.Contains('.'))
            ? ResolvePath(origin, unlocatedProject)
            : "";
        return new BuildDiagnostic(
            filePath, 0, 0,
            SeverityOf(match.Groups["sev"].Value),
            match.Groups["code"].Value,
            match.Groups["msg"].Value.Trim(),
            unlocatedProject);
    }

    /// <summary>Every distinct diagnostic in the output, in the order first seen.</summary>
    public static IReadOnlyList<BuildDiagnostic> Parse(IEnumerable<string> lines)
    {
        var seen = new HashSet<BuildDiagnostic>();
        var result = new List<BuildDiagnostic>();
        foreach (var line in lines)
        {
            if (TryParseLine(line) is { } diagnostic && seen.Add(diagnostic))
                result.Add(diagnostic);
        }
        return result;
    }

    private static string? Group(Match match, string name) => match.Groups[name].Success ? match.Groups[name].Value.Trim() : null;

    private static int SeverityOf(string text) => text.Equals("error", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    /// <summary>A relative path is relative to the project's folder, when the line says which project.</summary>
    private static string ResolvePath(string path, string? project)
    {
        if (path.Length == 0 || Path.IsPathRooted(path))
            return path;
        if (project is not null && Path.IsPathRooted(project) && Path.GetDirectoryName(project) is { } folder)
            return Path.GetFullPath(Path.Combine(folder, path));
        return path;
    }
}
