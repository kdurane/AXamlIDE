using System;
using System.Collections.Generic;
using System.Linq;
using AXamlIDE.RoslynExtension;
using AXamlIDE.Services.Testing;

namespace AXamlIDE.ViewModels;

/// <summary>The Test Explorer's data (what each test project has, and its results) and the tree built from it.</summary>
public sealed partial class TestExplorerViewModel
{
    /// <summary>
    /// One test project: its tests by full name, their last results (with the run each came from), and which are
    /// running now. Tests come from three places: reading the code (<see cref="SetScanned"/>, at once, a row per
    /// method), <c>dotnet test --list-tests</c> (<see cref="SetDiscovered"/>, after a build, a row per data case too)
    /// and the runs' results (<see cref="AddResults"/>).
    /// </summary>
    private sealed class ProjectTests(string path)
    {
        public string Path { get; } = path;
        public Dictionary<string, TestCaseName> Tests { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TestResult> Results { get; } = new(StringComparer.Ordinal);
        /// <summary>Which run each result came from (<see cref="TestExplorerViewModel"/>'s run number).</summary>
        public Dictionary<string, int> ResultRuns { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Running { get; } = new(StringComparer.Ordinal);
        /// <summary>Where each method is written ("Ns.Class.Method" to its file, line and column, 0-based), from the code.</summary>
        public Dictionary<string, (string Path, int Line, int Column)> Locations { get; } = new(StringComparer.Ordinal);
        public bool BuildFailed { get; set; }

        /// <summary>A fresh list: tests that are gone are dropped (with their results).</summary>
        public void SetDiscovered(IReadOnlyList<TestCaseName> tests)
        {
            // MSTest lists bare method names; a test read from the code with that method name (and only one) is
            // the same test with its class, so it stays and the bare name isn't added.
            var byMethod = Tests.Values.Where(test => test.ClassName.Length > 0)
                .GroupBy(test => test.MethodName, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var listed = new HashSet<string>(StringComparer.Ordinal);
            var added = new List<TestCaseName>();
            foreach (var test in tests)
            {
                if (test.ClassName.Length == 0 && byMethod.TryGetValue(test.MethodName, out var known))
                    listed.Add(known.FullName);
                else
                {
                    listed.Add(test.FullName);
                    added.Add(test);
                }
            }

            foreach (var gone in Tests.Keys.Where(name => !listed.Contains(name)).ToList())
                Forget(gone);
            foreach (var test in added)
                Tests.TryAdd(test.FullName, test);   // keep a name a run filled in (MSTest's class)
        }

        /// <summary>
        /// The test methods read from the code. A method that's gone goes (with its cases and results); a new one is
        /// added as a row of its own, which a run (or --list-tests) replaces with its data cases, if it has any.
        /// </summary>
        public void SetScanned(IReadOnlyList<TestMethodEntry> methods)
        {
            var names = methods.Select(method => $"{method.ClassName}.{method.MethodName}").ToHashSet(StringComparer.Ordinal);
            foreach (var gone in Tests.Where(pair => !names.Contains(pair.Value.MethodFullName)).Select(pair => pair.Key).ToList())
                Forget(gone);

            var present = Tests.Values.Select(test => test.MethodFullName).ToHashSet(StringComparer.Ordinal);
            Locations.Clear();
            foreach (var method in methods)
            {
                var name = $"{method.ClassName}.{method.MethodName}";
                if (method.Path is { } path)
                    Locations[name] = (path, method.Line, method.Column);
                if (present.Add(name))
                    Tests[name] = new TestCaseName(name, method.ClassName, method.MethodName, null);
            }
        }

        private void Forget(string name)
        {
            Tests.Remove(name);
            Results.Remove(name);
            ResultRuns.Remove(name);
        }

        /// <summary>These tests (null = all of them) are about to run.</summary>
        public void MarkRunning(IReadOnlyCollection<string>? tests)
        {
            Running.Clear();
            Running.UnionWith(tests ?? Tests.Keys);
        }

        /// <summary>
        /// A run's results. A result's name has the class even when discovery didn't (MSTest), so it replaces the
        /// listed one. Tests that were meant to run but have no result lose their old one (they didn't run).
        /// </summary>
        public void AddResults(IReadOnlyList<TestResult> results, IReadOnlyCollection<string>? ran, int run)
        {
            foreach (var name in ran ?? Tests.Keys.ToList())
            {
                Results.Remove(name);
                ResultRuns.Remove(name);
            }
            foreach (var result in results)
            {
                // A data case's result replaces its method's own row (read from the code, before its cases were known).
                if (result.Name.Arguments is not null && Tests.TryGetValue(result.Name.MethodFullName, out var method) && method.Arguments is null)
                    Forget(method.FullName);
                Tests[result.Name.FullName] = result.Name;
                Results[result.Name.FullName] = result;
                ResultRuns[result.Name.FullName] = run;
            }

            Running.Clear();
        }

        public TestOutcome OutcomeOf(TestCaseName test)
            => Running.Contains(test.FullName) ? TestOutcome.Running
                : Results.TryGetValue(test.FullName, out var result) ? result.Outcome
                : TestOutcome.NotRun;
    }

    private readonly Dictionary<string, ProjectTests> _projects = new(StringComparer.Ordinal);

    /// <summary>Counts the runs; results from before the latest show as out of date (Visual Studio's outline icons).</summary>
    private int _runNumber;

    private ProjectTests ProjectFor(string path)
    {
        if (!_projects.TryGetValue(path, out var project))
            _projects[path] = project = new ProjectTests(path);
        return project;
    }

    /// <summary>Projects that aren't test projects any more (or left the workspace) leave the tree.</summary>
    private void ForgetProjectsOtherThan(IReadOnlyCollection<string> testProjects)
    {
        foreach (var path in _projects.Keys.Where(path => !testProjects.Contains(path)).ToList())
            _projects.Remove(path);
        foreach (var path in testProjects)
            ProjectFor(path);
    }

    // ------------------------------------------------------------- the tree

    /// <summary>
    /// Build the tree again from the data, keeping which rows were expanded and which was selected. The first time a
    /// project shows, it's expanded; with <paramref name="expandFailures"/> so is every row leading to a failure.
    /// </summary>
    private void RebuildTree(bool expandFailures = false)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Roots.SelectMany(Flatten))
        {
            seen.Add(node.Key);
            if (node.IsExpanded)
                expanded.Add(node.Key);
        }

        var selectedKey = SelectedNode?.Key;
        Roots.Clear();
        foreach (var project in _projects.Values.OrderBy(p => System.IO.Path.GetFileName(p.Path), StringComparer.OrdinalIgnoreCase))
        {
            var root = BuildProject(project, _runNumber);
            foreach (var node in Flatten(root))
            {
                node.IsExpanded = expanded.Contains(node.Key)
                                  || (node.Kind == TestNodeKind.Project && !seen.Contains(node.Key))
                                  || (expandFailures && node.Outcome == TestOutcome.Failed && node.Children.Count > 0);
            }

            Roots.Add(root);
        }

        SelectedNode = selectedKey is null ? null : Roots.SelectMany(Flatten).FirstOrDefault(node => node.Key == selectedKey);
        UpdateCounts();
    }

    private static IEnumerable<TestNode> Flatten(TestNode node) => node.Children.SelectMany(Flatten).Prepend(node);

    /// <summary>Project → namespace → class → method → data cases, alphabetically, as Visual Studio groups them.</summary>
    private static TestNode BuildProject(ProjectTests project, int latestRun)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(project.Path) + (project.BuildFailed ? " (didn't build)" : "");
        var root = new TestNode(TestNodeKind.Project, name, project.Path, project.Path);

        foreach (var byNamespace in project.Tests.Values
                     .GroupBy(test => NamespaceOf(test.ClassName))
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            // MSTest lists bare method names until a run fills in their classes: those go straight under the project.
            var namespaceNode = root;
            if (byNamespace.Key.Length > 0 || byNamespace.Any(test => test.ClassName.Length > 0))
            {
                var namespaceKey = $"{project.Path}|{byNamespace.Key}";
                namespaceNode = new TestNode(TestNodeKind.Namespace, byNamespace.Key.Length == 0 ? "(no namespace)" : byNamespace.Key,
                    project.Path, namespaceKey) { Prefix = byNamespace.Key };
                root.Children.Add(namespaceNode);
            }

            foreach (var byClass in byNamespace.GroupBy(test => test.ClassName).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var classNode = namespaceNode;
                if (byClass.Key.Length > 0)
                {
                    classNode = new TestNode(TestNodeKind.Class, ShortName(byClass.Key), project.Path, $"{project.Path}|{byClass.Key}")
                        { Prefix = byClass.Key };
                    namespaceNode.Children.Add(classNode);
                }

                foreach (var byMethod in byClass.GroupBy(test => test.MethodName).OrderBy(group => group.Key, StringComparer.Ordinal))
                    classNode.Children.Add(BuildMethod(project, byClass.Key, byMethod.Key, [.. byMethod], latestRun));
            }
        }

        Summarise(root);
        return root;
    }

    /// <summary>A method's row: the test itself, or (for a data-driven method) a row per case under it.</summary>
    private static TestNode BuildMethod(ProjectTests project, string className, string methodName, List<TestCaseName> cases, int latestRun)
    {
        var methodKey = $"{project.Path}|{className}|{methodName}";
        if (cases.Count == 1 && cases[0].Arguments is null)
            return Leaf(project, TestNodeKind.Method, methodName, methodKey, cases[0], latestRun);

        var method = new TestNode(TestNodeKind.Method, methodName, project.Path, methodKey) { Test = cases[0] };
        foreach (var test in cases.OrderBy(test => test.FullName, StringComparer.Ordinal))
            method.Children.Add(Leaf(project, TestNodeKind.Case, test.Arguments ?? test.FullName, $"{methodKey}|{test.FullName}", test, latestRun));
        return method;
    }

    private static TestNode Leaf(ProjectTests project, TestNodeKind kind, string name, string key, TestCaseName test, int latestRun)
    {
        project.Results.TryGetValue(test.FullName, out var result);
        var outcome = project.OutcomeOf(test);
        return new TestNode(kind, name, project.Path, key)
        {
            Test = test,
            Result = result,
            Outcome = outcome,
            IsStale = outcome is not (TestOutcome.Running or TestOutcome.NotRun)
                      && project.ResultRuns.TryGetValue(test.FullName, out var run) && run < latestRun,
            Duration = result?.Duration ?? TimeSpan.Zero,
            TestCount = 1
        };
    }

    /// <summary>
    /// A group's outcome, count and time from its tests, by Visual Studio's rule: running, then failed (any test
    /// failed), passed (some passed, none failed: running a few of a class's tests makes it green, not "not run"),
    /// skipped, and not run only when nothing under it has run. Out of date when every result under it is.
    /// </summary>
    private static void Summarise(TestNode node)
    {
        if (node.Children.Count == 0)
            return;
        foreach (var child in node.Children)
            Summarise(child);

        node.TestCount = node.Children.Sum(child => child.TestCount);
        node.Duration = node.Children.Aggregate(TimeSpan.Zero, (sum, child) => sum + child.Duration);
        node.Outcome = GroupOutcome(node.Children.Select(child => child.Outcome).ToList());
        var ran = node.Children.Where(child => child.Outcome is not (TestOutcome.Running or TestOutcome.NotRun)).ToList();
        node.IsStale = ran.Count > 0 && ran.All(child => child.IsStale);
    }

    internal static TestOutcome GroupOutcome(IReadOnlyCollection<TestOutcome> outcomes)
        => outcomes.Contains(TestOutcome.Running) ? TestOutcome.Running
            : outcomes.Contains(TestOutcome.Failed) ? TestOutcome.Failed
            : outcomes.Contains(TestOutcome.Passed) ? TestOutcome.Passed
            : outcomes.Contains(TestOutcome.Skipped) ? TestOutcome.Skipped
            : TestOutcome.NotRun;

    private void UpdateCounts()
    {
        var outcomes = _projects.Values.SelectMany(project => project.Tests.Values.Select(project.OutcomeOf)).ToList();
        PassedCount = outcomes.Count(outcome => outcome == TestOutcome.Passed);
        FailedCount = outcomes.Count(outcome => outcome == TestOutcome.Failed);
        SkippedCount = outcomes.Count(outcome => outcome == TestOutcome.Skipped);
        NotRunCount = outcomes.Count(outcome => outcome is TestOutcome.NotRun or TestOutcome.Running);
    }

    private static string NamespaceOf(string className)
    {
        var dot = className.LastIndexOf('.');
        return dot < 0 ? "" : className[..dot];
    }

    private static string ShortName(string className)
    {
        var dot = className.LastIndexOf('.');
        return dot < 0 ? className : className[(dot + 1)..];
    }
}
