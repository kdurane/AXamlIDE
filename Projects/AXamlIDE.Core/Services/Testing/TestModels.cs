using System;
using System.Collections.Generic;

namespace AXamlIDE.Services.Testing;

public enum TestOutcome
{
    NotRun,
    Running,
    Passed,
    Failed,
    Skipped
}

/// <summary>
/// A test as <c>dotnet test</c> names it. <see cref="FullName"/> is the name the test framework gives it and is what
/// identifies it between discovery and a run: xUnit's "Ns.Class.Method(x: 1)", MSTest's "Method".
/// <see cref="ClassName"/> is "Ns.Class", or empty when not known (MSTest's discovery only gives the method).
/// <see cref="Arguments"/> is a data-driven case's "(x: 1)", else null.
/// </summary>
public sealed record TestCaseName(string FullName, string ClassName, string MethodName, string? Arguments)
{
    /// <summary>
    /// A name from <c>dotnet test --list-tests</c>: "Ns.Class.Method" or "Ns.Class.Method(args)" (the arguments
    /// can hold dots, so the class is split off before the first '(').
    /// </summary>
    public static TestCaseName Parse(string fullName)
    {
        var open = fullName.IndexOf('(');
        var head = open < 0 ? fullName : fullName[..open];
        var arguments = open < 0 ? null : fullName[open..];
        var dot = head.LastIndexOf('.');
        return dot < 0
            ? new TestCaseName(fullName, "", head.Trim(), arguments)
            : new TestCaseName(fullName, head[..dot], head[(dot + 1)..].Trim(), arguments);
    }

    /// <summary>"Ns.Class.Method": what <c>--filter FullyQualifiedName=</c> matches (no arguments).</summary>
    public string MethodFullName => ClassName.Length == 0 ? MethodName : $"{ClassName}.{MethodName}";
}

/// <summary>One test's result from a run.</summary>
public sealed record TestResult(
    TestCaseName Name,
    TestOutcome Outcome,
    TimeSpan Duration,
    string? ErrorMessage,
    string? StackTrace,
    string? Output);

/// <summary>A <c>dotnet test</c> run of one project: its exit code and the tests' results (empty if it didn't build).</summary>
public sealed record TestRunReport(int ExitCode, IReadOnlyList<TestResult> Results);
