using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace AXamlIDE.Services.Testing;

/// <summary>
/// Reads a .trx file (Visual Studio's test results format, written by <c>dotnet test --logger trx</c>).
///
///   TestDefinitions/UnitTest[@id]/TestMethod[@className, @name]   which class and method each test is
///   Results/UnitTestResult[@testId, @testName, @outcome, @duration]
///       Output/StdOut, Output/ErrorInfo/Message, Output/ErrorInfo/StackTrace
/// </summary>
public static class TrxReader
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>The results in the file; empty when it's missing or can't be read.</summary>
    public static IReadOnlyList<TestResult> ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Read(XDocument.Load(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return [];
        }
    }

    public static IReadOnlyList<TestResult> Read(XDocument trx)
    {
        var methods = new Dictionary<string, (string ClassName, string MethodName)>();
        foreach (var unitTest in trx.Descendants(Ns + "UnitTest"))
        {
            if ((string?)unitTest.Attribute("id") is { } id && unitTest.Element(Ns + "TestMethod") is { } method)
                methods[id] = ((string?)method.Attribute("className") ?? "", (string?)method.Attribute("name") ?? "");
        }

        var results = new List<TestResult>();
        foreach (var result in trx.Descendants(Ns + "UnitTestResult"))
        {
            var testName = (string?)result.Attribute("testName") ?? "";
            var name = methods.TryGetValue((string?)result.Attribute("testId") ?? "", out var method)
                ? NameFrom(testName, method.ClassName, method.MethodName)
                : TestCaseName.Parse(testName);

            var output = result.Element(Ns + "Output");
            var error = output?.Element(Ns + "ErrorInfo");
            results.Add(new TestResult(
                name,
                OutcomeFrom((string?)result.Attribute("outcome")),
                TimeSpan.TryParse((string?)result.Attribute("duration"), out var duration) ? duration : TimeSpan.Zero,
                NullIfEmpty(error?.Element(Ns + "Message")?.Value),
                NullIfEmpty(error?.Element(Ns + "StackTrace")?.Value),
                NullIfEmpty(output?.Element(Ns + "StdOut")?.Value)));
        }

        return results;
    }

    /// <summary>The definition knows the class and method; the result's name may add a data case's arguments.</summary>
    private static TestCaseName NameFrom(string testName, string className, string methodName)
    {
        // Some adapters write "Class.Method" as the method's name; keep the method.
        var dot = methodName.LastIndexOf('.');
        if (dot >= 0)
            methodName = methodName[(dot + 1)..];
        var open = testName.IndexOf('(');
        return new TestCaseName(testName, className, methodName, open < 0 ? null : testName[open..]);
    }

    /// <summary>TRX's outcomes; anything else (Error, Timeout, Aborted...) is a failure.</summary>
    private static TestOutcome OutcomeFrom(string? outcome) => outcome switch
    {
        "Passed" => TestOutcome.Passed,
        "NotExecuted" or "Inconclusive" or "Pending" or "Disconnected" => TestOutcome.Skipped,
        _ => TestOutcome.Failed
    };

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
