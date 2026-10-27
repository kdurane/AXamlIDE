using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AXamlIDE.Services.Testing;

/// <summary>
/// Builds <c>dotnet test --filter</c> expressions (VSTest's syntax: "FullyQualifiedName=A|FullyQualifiedName=B",
/// "~" for contains). Every test framework's adapter understands FullyQualifiedName and Name.
/// </summary>
public static class TestFilter
{
    /// <summary>
    /// Above this length a filter is dropped and the whole project runs: command lines have limits, and a filter
    /// this long is most of the project anyway.
    /// </summary>
    public const int MaximumLength = 20_000;

    /// <summary>These methods (every data case of each). Null when there are none, or too many to list.</summary>
    public static string? ForMethods(IEnumerable<TestCaseName> tests)
    {
        var parts = tests
            .Select(test => test.ClassName.Length == 0
                ? $"Name={Escape(test.MethodName)}"   // the class isn't known (MSTest before a run)
                : $"FullyQualifiedName={Escape(test.MethodFullName)}")
            .Distinct()
            .ToList();
        if (parts.Count == 0)
            return null;
        var filter = string.Join('|', parts);
        return filter.Length > MaximumLength ? null : filter;
    }

    /// <summary>Every test whose full name starts with this class's or namespace's (e.g. "Ns.Class").</summary>
    public static string ForPrefix(string classOrNamespace) => $"FullyQualifiedName~{Escape(classOrNamespace + ".")}";

    /// <summary>VSTest's filter escapes: a backslash before each of \ ( ) &amp; | = ! ~.</summary>
    public static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
                escaped.Append('\\');
            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
