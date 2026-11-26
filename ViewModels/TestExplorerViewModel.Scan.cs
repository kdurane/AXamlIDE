using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.RoslynExtension;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The tests read from the code: our extension in the C# language server lists each test project's test methods
/// from its compilation (unsaved edits included, nothing built), so the tree shows them as soon as the solution has
/// loaded, without waiting for <c>dotnet test --list-tests</c> to build every test project. Read again when a C#
/// file is saved. Refresh still builds and lists (that's what shows a data-driven test's cases before it has run).
/// Without the extension (or the C# server), the panel lists the tests by building, as before.
/// </summary>
public sealed partial class TestExplorerViewModel
{
    private readonly LspManager _servers;
    // A scan is going on; another was asked for meanwhile (a save, the projects loading) and runs after it.
    private bool _scanning;
    private bool _scanAgain;
    // Waiting for the C# server to load the solution before the first scan.
    private bool _scanWhenLoaded;

    /// <summary>
    /// Show the tests: read them from the code, or, when that can't be done, list them by building
    /// (<see cref="DiscoverCommand"/>).
    /// </summary>
    private async Task ShowTestsAsync()
    {
        if (!await ScanAsync())
            DiscoverCommand.Execute(null);
    }

    /// <summary>
    /// Read the tests from the code. False when that isn't possible here (no C# server, or no extension in it),
    /// so the caller lists them by building instead. While the server is still loading the solution, it says so
    /// and scans once the projects are loaded (<see cref="OnServersChanged"/>).
    /// </summary>
    private async Task<bool> ScanAsync()
    {
        if (_workspace.RootDirectory is null || !_servers.Handles(LanguageIds.CSharp))
            return false;
        if (_scanning)
        {
            _scanAgain = true;
            return true;
        }

        if (!_servers.HasLoadedProjects(LanguageIds.CSharp))
        {
            _scanWhenLoaded = true;
            if (_projects.Count == 0)
                Status = "Reading the tests once the C# language server has loaded the solution...";
            return true;
        }

        _scanning = true;
        try
        {
            if (await _servers.GetClientAsync(LanguageIds.CSharp) is not { } client
                || (await RoslynExtensionClient.ForAsync(client)).Extension is not { SupportsTests: true } extension)
                return false;
            if (IsBusy)
            {
                _scanAgain = true;   // a run or a listing is using the data; read them when it's done (End)
                return true;
            }

            var testProjects = await FindTestProjectsAsync();
            var tests = await ReadTestsAsync(extension, testProjects);
            if (tests is null || IsBusy)
            {
                _scanAgain |= IsBusy;
                return tests is not null;
            }

            ForgetProjectsOtherThan(testProjects);
            foreach (var (path, methods) in tests)
                ProjectFor(path).SetScanned(methods);
            RebuildTree();
            var count = _projects.Values.Sum(project => project.Tests.Count);
            Status = testProjects.Count == 0
                ? "No test projects. Use New Test Project to add one."
                : $"{count} tests in {testProjects.Count} project(s). Refresh builds them and lists each data case.";
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[tests] reading the tests from the code failed: {ex.Message}");
            return false;
        }
        finally
        {
            _scanning = false;
            if (_scanAgain)
            {
                _scanAgain = false;
                if (!IsBusy)
                    _ = ScanAsync();
            }
        }
    }

    /// <summary>Each test project's test methods (a project built for several frameworks once), or null if the
    /// server stopped meanwhile.</summary>
    private async Task<Dictionary<string, IReadOnlyList<TestMethodEntry>>?> ReadTestsAsync(
        RoslynExtensionClient extension, IReadOnlyList<string> testProjects)
    {
        var generation = _servers.Generation;
        var projects = await extension.ProjectsAsync([], CancellationToken.None);
        var tests = new Dictionary<string, IReadOnlyList<TestMethodEntry>>(StringComparer.Ordinal);
        foreach (var path in testProjects)
        {
            var methods = new List<TestMethodEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in projects.Where(project => project.FilePath is { } file && GitStatus.PathComparer.Equals(file, path)))
            {
                if (await extension.TestsAsync(project.Id, CancellationToken.None) is not { } answer)
                    continue;
                methods.AddRange(answer.Tests.Where(test => seen.Add($"{test.ClassName}.{test.MethodName}")));
            }
            tests[path] = methods;
        }
        return generation == _servers.Generation ? tests : null;
    }

    /// <summary>The C# server finished loading the solution: the scan that was waiting for it.</summary>
    private void OnServersChanged()
    {
        if (_scanWhenLoaded && _servers.HasLoadedProjects(LanguageIds.CSharp))
        {
            _scanWhenLoaded = false;
            _ = ShowTestsAsync();
        }
    }

    /// <summary>A C# file was saved: its tests may have changed (Visual Studio updates its Test Explorer on save too).</summary>
    private void OnDocumentSaved(OpenDocument document)
    {
        if (_shown && Path.GetExtension(document.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            _ = ScanAsync();
    }

    /// <summary>Where the code says a test method is (file, 1-based line, 0-based column), if it was read from it.</summary>
    private (string Path, int Line, int Column)? ScannedLocation(TestNode node)
        => node.Test is { } test && _projects.TryGetValue(node.ProjectPath, out var project)
           && project.Locations.TryGetValue(test.MethodFullName, out var location)
            ? (location.Path, location.Line + 1, location.Column)
            : null;
}
