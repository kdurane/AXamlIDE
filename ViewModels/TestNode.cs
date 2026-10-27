using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AXamlIDE.Services.Testing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.ViewModels;

public enum TestNodeKind { Project, Namespace, Class, Method, Case }

/// <summary>
/// A row of the Test Explorer's tree, grouped as Visual Studio does: project, namespace, class, test method, and
/// the data cases of a [Theory] / [TestCase] method. The tree is rebuilt after discovery and each run; rows keep
/// their expanded state across rebuilds by <see cref="Key"/>.
/// </summary>
public sealed partial class TestNode : ObservableObject
{
    public TestNode(TestNodeKind kind, string name, string projectPath, string key)
    {
        Kind = kind;
        Name = name;
        ProjectPath = projectPath;
        Key = key;
    }

    public TestNodeKind Kind { get; }

    /// <summary>What the row shows: the project, namespace, class (without namespace), method, or a case's arguments.</summary>
    public string Name { get; }

    public string ProjectPath { get; }

    /// <summary>Identifies the row between rebuilds ("project|Ns|Ns.Class|Method|case").</summary>
    public string Key { get; }

    /// <summary>A namespace's or class's full name ("Ns", "Ns.Class"): running it runs the tests under that name.</summary>
    public string? Prefix { get; init; }

    /// <summary>A method's or case's test (for a method with several cases, its first: they share class and method).</summary>
    public TestCaseName? Test { get; init; }

    /// <summary>A single test's last result (a case, or a method without cases).</summary>
    public TestResult? Result { get; init; }

    public TestOutcome Outcome { get; set; }

    /// <summary>How many tests are under it (1 for a single test).</summary>
    public int TestCount { get; set; }

    public TimeSpan Duration { get; set; }

    public ObservableCollection<TestNode> Children { get; } = [];

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>"(12)" after a group's name; nothing for a single test.</summary>
    public string CountText => Children.Count == 0 ? "" : $"({TestCount})";

    /// <summary>"12 ms" / "1.2 s" for a test that has run; nothing otherwise.</summary>
    public string DurationText => Outcome is TestOutcome.NotRun or TestOutcome.Running ? "" : FormatDuration(Duration);

    public bool IsSingleTest => Result is not null || (Test is not null && Children.Count == 0);

    /// <summary>The failure's stack trace, a line per frame, with links where a frame names a source file.</summary>
    public IReadOnlyList<StackFrameLine> StackFrames => StackTraceFrames.Parse(Result?.StackTrace);

    public string OutcomeText => Outcome switch
    {
        TestOutcome.Passed => "Passed",
        TestOutcome.Failed => "Failed",
        TestOutcome.Skipped => "Skipped",
        TestOutcome.Running => "Running",
        _ => "Not run"
    };

    /// <summary>The leaf tests under this row (itself, for a single test).</summary>
    public IEnumerable<TestNode> Leaves()
    {
        if (Children.Count == 0)
        {
            if (Test is not null)
                yield return this;
            yield break;
        }

        foreach (var child in Children)
            foreach (var leaf in child.Leaves())
                yield return leaf;
    }

    public static string FormatDuration(TimeSpan duration) => duration.TotalSeconds >= 1
        ? $"{duration.TotalSeconds:0.0} s"
        : $"{Math.Max(1, (int)Math.Round(duration.TotalMilliseconds))} ms";
}
