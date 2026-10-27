using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Testing;

/// <summary>A test framework "New Test Project" can make, and its <c>dotnet new</c> template.</summary>
public sealed record TestFramework(string Name, string Template);

/// <summary>
/// Visual Studio's Add > New Project > xUnit Test Project, for a project that has none: <c>dotnet new</c> the test
/// project beside the project under test, reference that project, and add it to the solution.
/// </summary>
public static class TestProjectCreator
{
    public static IReadOnlyList<TestFramework> Frameworks { get; } =
    [
        new("xUnit", "xunit"),
        new("NUnit", "nunit"),
        new("MSTest", "mstest")
    ];

    /// <summary>Where the new project goes: a sibling folder of the project under test's folder.</summary>
    public static string FolderFor(string projectUnderTest, string testProjectName)
    {
        var projectFolder = Path.GetDirectoryName(Path.GetFullPath(projectUnderTest))!;
        var parent = Path.GetDirectoryName(projectFolder) ?? projectFolder;
        return Path.Combine(parent, testProjectName);
    }

    /// <summary>The new project's path, or null with dotnet's output when a step failed.</summary>
    public static async Task<(string? ProjectPath, string Output)> CreateAsync(
        TestFramework framework,
        string name,
        string projectUnderTest,
        string? solutionPath,
        CancellationToken ct)
    {
        var output = new StringBuilder();
        void Collect(string line) => output.AppendLine(line);

        var folder = FolderFor(projectUnderTest, name);
        if (Directory.Exists(folder))
            return (null, $"{folder} already exists.");
        var parent = Path.GetDirectoryName(folder)!;
        var testProject = Path.Combine(folder, name + ".csproj");

        if (await DotNetProcess.RunAsync(parent, ["new", framework.Template, "--name", name, "--output", folder], Collect, ct) != 0)
            return (null, output.ToString());
        if (await DotNetProcess.RunAsync(folder, ["add", testProject, "reference", projectUnderTest], Collect, ct) != 0)
            return (null, output.ToString());
        if (solutionPath is not null
            && await DotNetProcess.RunAsync(Path.GetDirectoryName(solutionPath)!, ["sln", solutionPath, "add", testProject], Collect, ct) != 0)
            return (null, output.ToString());

        return (testProject, output.ToString());
    }

    /// <summary>"MyApp.Tests" for MyApp.csproj (Visual Studio's suggestion).</summary>
    public static string SuggestedName(string projectUnderTest) => Path.GetFileNameWithoutExtension(projectUnderTest) + ".Tests";
}
