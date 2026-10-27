using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Testing;

/// <summary>
/// Finds and runs a project's tests with <c>dotnet test</c> (VSTest), as Visual Studio's Test Explorer does:
/// discovery is <c>--list-tests</c>, a run writes a .trx results file (<see cref="TrxReader"/>). Both build the
/// project first. Every line dotnet prints goes to <see cref="OutputReceived"/> (build errors show up there).
/// </summary>
public sealed class DotNetTestRunner
{
    private const string ListHeading = "The following Tests are available:";

    /// <summary>A line of dotnet's output, on a background thread.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>The project's tests, or null when it didn't build (or dotnet failed).</summary>
    public async Task<IReadOnlyList<TestCaseName>?> ListTestsAsync(string projectPath, string configuration, CancellationToken ct)
    {
        var lines = new List<string>();
        var exitCode = await DotNetProcess.RunAsync(
            Path.GetDirectoryName(projectPath)!,
            ["test", projectPath, "--list-tests", "--configuration", configuration],
            line =>
            {
                lines.Add(line);
                OutputReceived?.Invoke(line);
            },
            ct);
        return exitCode == 0 ? ParseTestList(lines) : null;
    }

    /// <summary>
    /// Run the project's tests (those matching <paramref name="filter"/>, see <see cref="TestFilter"/>; null = all).
    /// A failing test makes the exit code non-zero; the results say which.
    /// </summary>
    public async Task<TestRunReport> RunAsync(string projectPath, string configuration, string? filter, CancellationToken ct)
    {
        var resultsFolder = Path.Combine(Path.GetTempPath(), "AXamlIDE", "TestResults", Guid.NewGuid().ToString("N"));
        List<string> arguments =
        [
            "test", projectPath, "--configuration", configuration,
            "--logger", "trx;LogFileName=results.trx", "--results-directory", resultsFolder
        ];
        if (filter is not null)
            arguments.AddRange(["--filter", filter]);

        try
        {
            var exitCode = await DotNetProcess.RunAsync(
                Path.GetDirectoryName(projectPath)!, arguments, line => OutputReceived?.Invoke(line), ct);
            return new TestRunReport(exitCode, TrxReader.ReadFile(Path.Combine(resultsFolder, "results.trx")));
        }
        finally
        {
            try
            {
                if (Directory.Exists(resultsFolder))
                    Directory.Delete(resultsFolder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temp folder; harmless.
            }
        }
    }

    /// <summary>
    /// The names under "The following Tests are available:", one per indented line. A project that targets several
    /// frameworks lists each test once per framework; those are merged.
    /// </summary>
    public static IReadOnlyList<TestCaseName> ParseTestList(IEnumerable<string> lines)
    {
        var tests = new List<TestCaseName>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inList = false;
        foreach (var line in lines)
        {
            if (line.Trim() == ListHeading)
            {
                inList = true;
                continue;
            }

            if (!inList)
                continue;
            if (line.Length == 0 || !char.IsWhiteSpace(line[0]))
            {
                inList = false;   // the list has ended (the next project's build output, a summary...)
                continue;
            }

            var name = line.Trim();
            if (name.Length > 0 && seen.Add(name))
                tests.Add(TestCaseName.Parse(name));
        }

        return tests;
    }
}
