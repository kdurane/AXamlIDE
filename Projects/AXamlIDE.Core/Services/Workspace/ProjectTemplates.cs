using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Processes;

namespace AXamlIDE.Services.Workspace;

/// <summary>A project template installed for <c>dotnet new</c> ("Avalonia .NET MVVM App", avalonia.mvvm).</summary>
public sealed record ProjectTemplate(string Name, string ShortName, IReadOnlyList<string> Languages, IReadOnlyList<string> Tags)
{
    public bool IsAvalonia => ShortName.StartsWith("avalonia", StringComparison.OrdinalIgnoreCase)
                              || Tags.Contains("Avalonia", StringComparer.OrdinalIgnoreCase);

    /// <summary>"C#, F#  ·  Desktop, Xaml, Avalonia": its languages and tags, for a list.</summary>
    public string Summary => string.Join("  ·  ", new[] { string.Join(", ", Languages), string.Join(", ", Tags) }.Where(part => part.Length > 0));
}

/// <summary>What the New Project wizard asks for, as in Visual Studio's "Configure your new project".</summary>
public sealed record NewProjectRequest(
    ProjectTemplate Template,
    string ProjectName,
    string Location,
    string SolutionName,
    bool SolutionInProjectFolder)
{
    /// <summary>
    /// Where things go. Together: Location/Name holds the solution and the project. Apart (Visual Studio's default):
    /// Location/SolutionName holds the solution, with the project in a folder of its own inside it.
    /// </summary>
    public string SolutionFolder => SolutionInProjectFolder
        ? Path.Combine(Location, ProjectName)
        : Path.Combine(Location, SolutionName);

    public string ProjectFolder => Path.Combine(SolutionFolder, SolutionInProjectFolder ? "" : ProjectName).TrimEnd(Path.DirectorySeparatorChar);
}

/// <summary>Lists the installed project templates and creates a solution from one (the New Project wizard).</summary>
public static class ProjectTemplates
{
    public const string AvaloniaTemplatesPackage = "Avalonia.Templates";

    private static readonly Dictionary<string, string> Environment = new()
    {
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    };

    /// <summary>The installed project templates (<c>dotnet new list --type project</c>); null when dotnet couldn't list them.</summary>
    public static async Task<IReadOnlyList<ProjectTemplate>?> ListAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        try
        {
            var exitCode = await LineProcess.RunAsync("dotnet", System.Environment.CurrentDirectory,
                ["new", "list", "--type", "project"], line => { lock (lines) lines.Add(line); }, ct, Environment);
            if (exitCode != 0)
                return null;
        }
        catch (Win32Exception)
        {
            return null;
        }

        lock (lines)
            return ParseList(lines);
    }

    /// <summary>
    /// Reads <c>dotnet new list</c>'s table. Its line of dashes under the headings gives each column's start and
    /// width, so names with spaces in them ("Console App") are read whole. Lines before the headings are skipped.
    /// </summary>
    public static IReadOnlyList<ProjectTemplate> ParseList(IReadOnlyList<string> lines)
    {
        var dashes = -1;
        for (var i = 0; i < lines.Count && dashes < 0; i++)
            if (lines[i].TrimStart().StartsWith("---", StringComparison.Ordinal))
                dashes = i;
        if (dashes < 0)
            return [];

        var columns = Columns(lines[dashes]);
        if (columns.Count < 2)
            return [];

        var templates = new List<ProjectTemplate>();
        foreach (var line in lines.Skip(dashes + 1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string Cell(int index) => index >= columns.Count || columns[index].Start >= line.Length
                ? ""
                : line.Substring(columns[index].Start, Math.Min(columns[index].Width, line.Length - columns[index].Start)).Trim();

            var name = Cell(0);
            var shortName = Cell(1).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (name.Length == 0 || shortName is null)
                continue;
            var languages = Cell(2).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(language => language.Trim('[', ']')).ToList();
            var tags = Cell(3).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            templates.Add(new ProjectTemplate(name, shortName, languages, tags));
        }

        return templates;
    }

    /// <summary>Each run of dashes: where the column starts and how wide it is (up to the next column, or the line's end).</summary>
    private static List<(int Start, int Width)> Columns(string dashLine)
    {
        var starts = new List<int>();
        for (var i = 0; i < dashLine.Length; i++)
            if (dashLine[i] == '-' && (i == 0 || dashLine[i - 1] == ' '))
                starts.Add(i);
        return [.. starts.Select((start, index) => (start, index + 1 < starts.Count ? starts[index + 1] - start : int.MaxValue / 2))];
    }

    /// <summary>Why the request can't be created (a bad name, a folder that's already there), or null when it can.</summary>
    public static string? Problem(NewProjectRequest request)
    {
        if (NameProblem(request.ProjectName, "project") is { } project)
            return project;
        if (!request.SolutionInProjectFolder && NameProblem(request.SolutionName, "solution") is { } solution)
            return solution;
        if (string.IsNullOrWhiteSpace(request.Location) || !Path.IsPathRooted(request.Location))
            return "Choose a location for the project.";
        var folder = request.SolutionFolder;
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            return $"'{folder}' already exists and isn't empty.";
        return null;
    }

    private static string? NameProblem(string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name))
            return $"Enter a {what} name.";
        if (name != name.Trim() || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            return $"The {what} name can't contain spaces at either end or any of these: / \\ : * ? \" < > |";
        if (name is "." or "..")
            return $"'{name}' can't be a {what} name.";
        return null;
    }

    /// <summary>
    /// Create the project from its template, then a solution holding it (and any other projects the template made,
    /// as multi-platform templates do). Returns the solution's path. Throws <see cref="InvalidOperationException"/>
    /// with dotnet's message when a step fails.
    /// </summary>
    public static async Task<string> CreateAsync(NewProjectRequest request, Action<string> onLine, CancellationToken ct)
    {
        if (Problem(request) is { } problem)
            throw new InvalidOperationException(problem);

        Directory.CreateDirectory(request.SolutionFolder);
        await RunAsync(request.SolutionFolder, ["new", request.Template.ShortName, "--name", request.ProjectName, "--output", request.ProjectFolder], onLine, ct);

        var solutionName = request.SolutionInProjectFolder ? request.ProjectName : request.SolutionName;
        var existing = SolutionsIn(request.SolutionFolder);
        if (existing.Count == 1)
            return existing[0];   // the template made its own solution

        await RunAsync(request.SolutionFolder, ["new", "sln", "--name", solutionName, "--output", request.SolutionFolder], onLine, ct);
        var solution = SolutionsIn(request.SolutionFolder).FirstOrDefault(path => Path.GetFileNameWithoutExtension(path) == solutionName)
                       ?? throw new InvalidOperationException("dotnet new sln didn't create a solution.");

        var projects = Directory.EnumerateFiles(request.ProjectFolder, "*.*proj", SearchOption.AllDirectories)
            .Where(path => path.EndsWith("proj", StringComparison.OrdinalIgnoreCase) && !IsBuildOutput(request.ProjectFolder, path))
            .ToList();
        if (projects.Count > 0)
            await RunAsync(request.SolutionFolder, ["sln", solution, "add", .. projects], onLine, ct);
        return solution;
    }

    /// <summary>Install the Avalonia templates (<c>dotnet new install Avalonia.Templates</c>).</summary>
    public static Task InstallAvaloniaTemplatesAsync(Action<string> onLine, CancellationToken ct)
        => RunAsync(System.Environment.CurrentDirectory, ["new", "install", AvaloniaTemplatesPackage], onLine, ct);

    private static async Task RunAsync(string folder, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken ct)
    {
        var lines = new List<string>();
        int exitCode;
        try
        {
            exitCode = await LineProcess.RunAsync("dotnet", folder, arguments, line =>
            {
                lock (lines) lines.Add(line);
                onLine(line);
            }, ct, Environment);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Couldn't run dotnet ({ex.Message}). Is the .NET SDK installed?");
        }

        if (exitCode != 0)
            lock (lines)
                throw new InvalidOperationException(
                    lines.LastOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))?.Trim()
                    ?? lines.LastOrDefault(line => line.Trim().Length > 0)?.Trim()
                    ?? $"dotnet {arguments[0]} {arguments[1]} failed ({exitCode}).");
    }

    private static List<string> SolutionsIn(string folder)
        => [.. Directory.EnumerateFiles(folder, "*.sln").Concat(Directory.EnumerateFiles(folder, "*.slnx")).Distinct()];

    private static bool IsBuildOutput(string root, string path)
        => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
            .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
}
