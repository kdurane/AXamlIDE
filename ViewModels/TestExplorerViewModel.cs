using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Testing;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Tests panel: Visual Studio's Test Explorer. Finds the workspace's test projects (TestProjectDetector), lists
/// their tests (<c>dotnet test --list-tests</c>) and runs all, the failed, or the selected ones, with each result in
/// the tree (TestExplorerViewModel.Tree.cs). Double-click goes to the test's code; a failure's stack trace links to
/// its lines. "New Test Project" sets up tests for a project that has none.
///
/// Tests are listed the first time the panel is shown (the view calls <see cref="EnsureDiscovered"/>), on Refresh,
/// and when another workspace opens while the panel has been shown. Listing and running build the projects, with
/// the toolbar's configuration.
/// </summary>
public sealed partial class TestExplorerViewModel : ObservableObject
{
    private const int MaximumLogLines = 5000;

    private readonly WorkspaceModel _workspace;
    private readonly WorkspaceViewModel _explorer;
    private readonly RunViewModel _run;
    private readonly DocumentSaveService _saver;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;
    private readonly DotNetTestRunner _runner = new();
    private CancellationTokenSource? _cancel;
    private bool _shown;

    public TestExplorerViewModel(
        WorkspaceModel workspace,
        WorkspaceViewModel explorer,
        RunViewModel run,
        DocumentSaveService saver,
        IDialogService dialogs,
        IMessenger messenger)
    {
        _workspace = workspace;
        _explorer = explorer;
        _run = run;
        _saver = saver;
        _dialogs = dialogs;
        _messenger = messenger;

        _runner.OutputReceived += line => Dispatcher.UIThread.Post(() => AddLog(line));
        explorer.Opened += OnWorkspaceOpened;
        explorer.Closed += Reset;
    }

    /// <summary>The tree: one row per test project.</summary>
    public ObservableCollection<TestNode> Roots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTest))]
    public partial TestNode? SelectedNode { get; set; }

    /// <summary>The selection is one test (its details show beside the tree).</summary>
    public bool HasSelectedTest => SelectedNode is { IsSingleTest: true };

    /// <summary>What dotnet printed while listing and running (shown with the Output toggle, and after a failed build).</summary>
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] public partial bool ShowLog { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCommand), nameof(RunAllCommand), nameof(RunFailedCommand),
        nameof(RunSelectedCommand), nameof(NewTestProjectCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial string Status { get; set; } = "";

    [ObservableProperty] public partial int PassedCount { get; set; }
    [ObservableProperty] public partial int FailedCount { get; set; }
    [ObservableProperty] public partial int SkippedCount { get; set; }
    [ObservableProperty] public partial int NotRunCount { get; set; }

    private bool CanStart => !IsBusy;

    // ------------------------------------------------------------- discovery

    /// <summary>The panel was shown: list the tests the first time.</summary>
    public void EnsureDiscovered()
    {
        if (_shown)
            return;
        _shown = true;
        if (_workspace.RootDirectory is not null && _projects.Count == 0)
            DiscoverCommand.Execute(null);
    }

    /// <summary>Refresh: find the test projects again and list their tests.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Discover()
    {
        if (!await BeginAsync("Finding tests..."))
            return;
        try
        {
            var testProjects = await FindTestProjectsAsync();
            ForgetProjectsOtherThan(testProjects);
            if (testProjects.Count == 0)
            {
                Status = _workspace.RootDirectory is null
                    ? "Open a folder or solution to see its tests."
                    : "No test projects. Use New Test Project to add one.";
                return;
            }

            var failed = 0;
            foreach (var path in testProjects)
            {
                _cancel!.Token.ThrowIfCancellationRequested();
                Status = $"Finding tests in {Path.GetFileNameWithoutExtension(path)}...";
                var tests = await _runner.ListTestsAsync(path, _run.Configuration, _cancel.Token);
                var project = ProjectFor(path);
                if (tests is null)
                {
                    project.BuildFailed = true;
                    failed++;
                    continue;
                }

                project.BuildFailed = false;
                project.SetDiscovered(tests);
            }

            RebuildTree();
            Status = failed > 0
                ? $"{failed} test project(s) didn't build. See the output."
                : $"{_projects.Values.Sum(p => p.Tests.Count)} tests in {testProjects.Count} project(s).";
            if (failed > 0)
                ShowLog = true;
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        finally
        {
            End();
        }
    }

    private void OnWorkspaceOpened()
    {
        Reset();
        if (_shown)
            DiscoverCommand.Execute(null);
    }

    private void Reset()
    {
        _cancel?.Cancel();
        _projects.Clear();
        Roots.Clear();
        SelectedNode = null;
        Log.Clear();
        Status = "";
        UpdateCounts();
    }

    private Task<IReadOnlyList<string>> FindTestProjectsAsync()
    {
        var projects = _workspace.Projects.ToArray();
        return Task.Run(() => TestProjectDetector.FindTestProjects(projects));
    }

    // ------------------------------------------------------------- running

    /// <summary>Run every test in every test project (Test > Run All Tests).</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunAll()
    {
        _messenger.Send(new ShowToolTabMessage(ToolTab.Tests));
        if (!await BeginAsync("Running tests..."))
            return;
        try
        {
            var testProjects = await FindTestProjectsAsync();
            ForgetProjectsOtherThan(testProjects);
            await RunAsync(testProjects.Select(path => (path, (string?)null, (IReadOnlyCollection<string>?)null)).ToList());
        }
        finally
        {
            End();
        }
    }

    /// <summary>Run the tests that failed last time.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunFailed()
    {
        _messenger.Send(new ShowToolTabMessage(ToolTab.Tests));
        var runs = new List<(string, string?, IReadOnlyCollection<string>?)>();
        foreach (var project in _projects.Values)
        {
            var failed = project.Results.Values.Where(result => result.Outcome == TestOutcome.Failed).Select(r => r.Name).ToList();
            if (failed.Count > 0)
                runs.Add((project.Path, TestFilter.ForMethods(failed), failed.Select(name => name.FullName).ToList()));
        }

        if (runs.Count == 0)
        {
            Status = "No failed tests to run.";
            return;
        }

        if (!await BeginAsync("Running failed tests..."))
            return;
        try
        {
            await RunAsync(runs);
        }
        finally
        {
            End();
        }
    }

    /// <summary>Run the tests under a row (its right-click menu), or under the selected row.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunSelected(TestNode? node)
    {
        node ??= SelectedNode;
        if (node is null)
        {
            Status = "Select a test, class or project to run.";
            return;
        }

        var filter = node.Kind switch
        {
            TestNodeKind.Project => null,
            TestNodeKind.Namespace or TestNodeKind.Class when !string.IsNullOrEmpty(node.Prefix) => TestFilter.ForPrefix(node.Prefix),
            _ => TestFilter.ForMethods(node.Leaves().Select(leaf => leaf.Test!))
        };
        var running = node.Leaves().Select(leaf => leaf.Test!.FullName).ToList();

        if (!await BeginAsync($"Running {node.Name}..."))
            return;
        try
        {
            await RunAsync([(node.ProjectPath, filter, running)]);
        }
        finally
        {
            End();
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cancel?.Cancel();

    /// <summary>
    /// Run each (project, filter) in turn. <paramref name="runs"/>' third part is the tests to show as running
    /// (null = all the project's).
    /// </summary>
    private async Task RunAsync(IReadOnlyList<(string Path, string? Filter, IReadOnlyCollection<string>? Tests)> runs)
    {
        if (runs.Count == 0)
        {
            Status = _workspace.RootDirectory is null
                ? "Open a folder or solution to run its tests."
                : "No test projects. Use New Test Project to add one.";
            return;
        }

        foreach (var (path, _, tests) in runs)
            ProjectFor(path).MarkRunning(tests);
        RebuildTree();

        var started = DateTime.Now;
        var buildFailures = 0;
        try
        {
            foreach (var (path, filter, tests) in runs)
            {
                _cancel!.Token.ThrowIfCancellationRequested();
                Status = $"Running tests in {Path.GetFileNameWithoutExtension(path)}...";
                var report = await _runner.RunAsync(path, _run.Configuration, filter, _cancel.Token);
                var project = ProjectFor(path);
                project.AddResults(report.Results, tests);
                project.BuildFailed = report.ExitCode != 0 && report.Results.Count == 0;
                if (project.BuildFailed)
                    buildFailures++;
                RebuildTree(expandFailures: true);
            }

            UpdateCounts();
            var summary = $"{PassedCount} passed, {FailedCount} failed, {SkippedCount} skipped ({TestNode.FormatDuration(DateTime.Now - started)})";
            Status = buildFailures > 0 ? $"{buildFailures} test project(s) didn't build or run. See the output. {summary}" : summary;
            _messenger.Send(new StatusMessage($"Tests: {summary}"));
            if (buildFailures > 0)
                ShowLog = true;
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        finally
        {
            foreach (var project in _projects.Values)
                project.Running.Clear();
            RebuildTree();
        }
    }

    // ------------------------------------------------------------- busy

    /// <summary>Save everything (listing and running build), then go busy. False when a save failed.</summary>
    private async Task<bool> BeginAsync(string status)
    {
        if (IsBusy || !await _saver.SaveAllAsync())
            return false;
        IsBusy = true;
        Status = status;
        _cancel = new CancellationTokenSource();
        AddLog($"---- {status}");
        return true;
    }

    private void End()
    {
        _cancel?.Dispose();
        _cancel = null;
        IsBusy = false;
    }

    private void AddLog(string line)
    {
        Log.Add(line);
        while (Log.Count > MaximumLogLines)
            Log.RemoveAt(0);
    }

    // ------------------------------------------------------------- going to code

    /// <summary>Double-click / Go to Test: open the test method's code.</summary>
    [RelayCommand]
    private async Task GoToTest(TestNode? node)
    {
        node ??= SelectedNode;
        if (node?.Test is not { } test)
            return;

        var folder = Path.GetDirectoryName(node.ProjectPath)!;
        var found = await Task.Run(() => TestSourceLocator.Find(folder, test.ClassName, test.MethodName));
        if (found is not { } location)
        {
            Status = $"Couldn't find {test.MethodName} in {Path.GetFileName(node.ProjectPath)}'s code.";
            return;
        }

        _messenger.Send(new NavigateToSearchResultMessage(
            new WorkspaceSearchResult(location.Path, location.Line, location.Column, ""), test.MethodName));
    }

    /// <summary>A stack trace line's link: open the file at that line.</summary>
    [RelayCommand]
    private void OpenFrame(StackFrameLine? frame)
    {
        if (frame?.Path is not { } path)
            return;
        if (!File.Exists(path))
        {
            Status = $"{path} isn't on this machine.";
            return;
        }

        _messenger.Send(new NavigateToSearchResultMessage(new WorkspaceSearchResult(path, frame.Line, 0, ""), ""));
    }

    // ------------------------------------------------------------- new test project

    /// <summary>
    /// Visual Studio's "Create Unit Tests" / Add > New Project > Test Project: make an xUnit, NUnit or MSTest
    /// project beside the project under test, referencing it, and add it to the solution.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task NewTestProject()
    {
        if (await ChooseProjectUnderTestAsync() is not { } projectUnderTest)
            return;

        var names = TestProjectCreator.Frameworks.Select(framework => framework.Name).ToList();
        if (await _dialogs.PickAsync("New Test Project", "Test framework", names) is not { } frameworkName)
            return;
        var framework = TestProjectCreator.Frameworks.First(f => f.Name == frameworkName);

        var name = await _dialogs.PromptForTextAsync(
            "New Test Project", "Project name", "Next", TestProjectCreator.SuggestedName(projectUnderTest));
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Trim();

        var folder = TestProjectCreator.FolderFor(projectUnderTest, name);
        var solution = _explorer.SolutionPath;
        var message = $"Create the {framework.Name} project {name} in\n{folder}\n\nIt will reference {Path.GetFileName(projectUnderTest)}"
                      + (solution is null ? "." : $" and be added to {Path.GetFileName(solution)}.");
        if (_workspace.RootDirectory is { } root && !IsInside(folder, root))
            message += "\n\nThat folder is outside the open folder, so the explorer won't show it. Open its parent folder to see both.";
        if (!await _dialogs.ConfirmAsync("New Test Project", message, "Create"))
            return;

        if (!await BeginAsync($"Creating {name}..."))
            return;
        string? created;
        try
        {
            (created, var output) = await TestProjectCreator.CreateAsync(framework, name, projectUnderTest, solution, _cancel!.Token);
            foreach (var line in output.Split('\n'))
                AddLog(line.TrimEnd('\r'));
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
            return;
        }
        finally
        {
            End();
        }

        if (created is null)
        {
            Status = $"Couldn't create {name}. See the output.";
            ShowLog = true;
            return;
        }

        Status = $"Created {name}.";
        _messenger.Send(new StatusMessage($"Created test project {name}."));
        if (_explorer.Roots.FirstOrDefault() is { } workspaceRoot)
            await _explorer.RefreshAsync(workspaceRoot);   // reopens: the new project joins the workspace
        await DiscoverCommand.ExecuteAsync(null);
    }

    /// <summary>The explorer selection's project, else the startup project, else ask (test projects aren't offered).</summary>
    private async Task<string?> ChooseProjectUnderTestAsync()
    {
        var candidates = _workspace.Projects.Where(path => !TestProjectDetector.IsTestProjectFile(path)).ToList();
        if (_workspace.ResolveProjectPath(_explorer.SelectedNode) is { } selected && candidates.Contains(selected))
            return selected;
        if (_run.StartupProjectPath is { } startup && candidates.Contains(startup))
            return startup;
        if (candidates.Count == 1)
            return candidates[0];
        if (candidates.Count == 0)
        {
            Status = _workspace.RootDirectory is null ? "Open a folder or solution first." : "There's no project to test.";
            return null;
        }

        var byName = candidates.ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);
        var picked = await _dialogs.PickAsync("New Test Project", "Project to test", [.. byName.Keys]);
        return picked is null ? null : byName[picked];
    }

    private static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }
}
