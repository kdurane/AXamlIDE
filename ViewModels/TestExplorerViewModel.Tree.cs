using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AXamlIDE.Services.Testing;

namespace AXamlIDE.ViewModels;

/// <summary>The Test Explorer's data (what each test project has, and its results) and the tree built from it.</summary>
public sealed partial class TestExplorerViewModel
{
    /// <summary>One test project: its tests by full name, their last results, and which are running now.</summary>
    private sealed class ProjectTests(string path)
    {
        public string Path { get; } = path;
        public Dictionary<string, TestCaseName> Tests { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TestResult> Results { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Running { get; } = new(StringComparer.Ordinal);
        public bool BuildFailed { get; set; }

        /// <summary>A fresh list: tests that are gone are dropped (with their results).</summary>
        public void SetDiscovered(IReadOnlyList<TestCaseName> tests)
        {
            var listed = tests.Select(test => test.FullName).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in Tests.Keys.Where(name => !listed.Contains(name)).ToList())
            {
                Tests.Remove(gone);
                Results.Remove(gone);
            }

            foreach (var test in tests)
                Tests.TryAdd(test.FullName, test);   // keep a name a run filled in (MSTest's class)
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
        public void AddResults(IReadOnlyList<TestResult> results, IReadOnlyCollection<string>? ran)
        {
            foreach (var name in ran ?? Tests.Keys.ToList())
                Results.Remove(name);
            foreach (var result in results)
            {
                Tests[result.Name.FullName] = result.Name;
                Results[result.Name.FullName] = result;
            }

            Running.Clear();
        }

        public TestOutcome OutcomeOf(TestCaseName test)
            => Running.Contains(test.FullName) ? TestOutcome.Running
                : Results.TryGetValue(test.FullName, out var result) ? result.Outcome
                : TestOutcome.NotRun;
    }

    private readonly Dictionary<string, ProjectTests> _projects = new(StringComparer.Ordinal);

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
            var root = BuildProject(project);
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
    private static TestNode BuildProject(ProjectTests project)
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
                    classNode.Children.Add(BuildMethod(project, byClass.Key, byMethod.Key, [.. byMethod]));
            }
        }

        Summarise(root);
        return root;
    }

    /// <summary>A method's row: the test itself, or (for a data-driven method) a row per case under it.</summary>
    private static TestNode BuildMethod(ProjectTests project, string className, string methodName, List<TestCaseName> cases)
    {
        var methodKey = $"{project.Path}|{className}|{methodName}";
        if (cases.Count == 1 && cases[0].Arguments is null)
            return Leaf(project, TestNodeKind.Method, methodName, methodKey, cases[0]);

        var method = new TestNode(TestNodeKind.Method, methodName, project.Path, methodKey) { Test = cases[0] };
        foreach (var test in cases.OrderBy(test => test.FullName, StringComparer.Ordinal))
            method.Children.Add(Leaf(project, TestNodeKind.Case, test.Arguments ?? test.FullName, $"{methodKey}|{test.FullName}", test));
        return method;
    }

    private static TestNode Leaf(ProjectTests project, TestNodeKind kind, string name, string key, TestCaseName test)
    {
        project.Results.TryGetValue(test.FullName, out var result);
        return new TestNode(kind, name, project.Path, key)
        {
            Test = test,
            Result = result,
            Outcome = project.OutcomeOf(test),
            Duration = result?.Duration ?? TimeSpan.Zero,
            TestCount = 1
        };
    }

    /// <summary>A group's outcome, count and time from its tests: running beats failed beats not run beats passed.</summary>
    private static void Summarise(TestNode node)
    {
        if (node.Children.Count == 0)
            return;
        foreach (var child in node.Children)
            Summarise(child);

        node.TestCount = node.Children.Sum(child => child.TestCount);
        node.Duration = node.Children.Aggregate(TimeSpan.Zero, (sum, child) => sum + child.Duration);
        var outcomes = node.Children.Select(child => child.Outcome).ToList();
        node.Outcome = outcomes.Contains(TestOutcome.Running) ? TestOutcome.Running
            : outcomes.Contains(TestOutcome.Failed) ? TestOutcome.Failed
            : outcomes.Contains(TestOutcome.NotRun) ? TestOutcome.NotRun
            : outcomes.Contains(TestOutcome.Passed) ? TestOutcome.Passed
            : TestOutcome.Skipped;
    }

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
