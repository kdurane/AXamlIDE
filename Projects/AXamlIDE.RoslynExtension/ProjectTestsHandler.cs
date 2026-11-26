using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;

namespace AXamlIDE.RoslynExtension;

/// <summary>
/// One project's test methods, read from the server's compilation of it (so unsaved edits count, and nothing is built):
/// what the Test Explorer shows straight away, before <c>dotnet test --list-tests</c> has built anything. Kept per
/// project version, so asking again after nothing changed is free.
/// </summary>
public sealed class ProjectTestsHandler : IExtensionWorkspaceMessageHandler<ProjectTestsRequest, ProjectTestsResponse>
{
    private static readonly ConcurrentDictionary<ProjectId, ProjectTestsResponse> Known = new();

    public async Task<ProjectTestsResponse> ExecuteAsync(ProjectTestsRequest message, ExtensionWorkspaceMessageContext context,
        CancellationToken cancellationToken)
    {
        if (DiagnosticCache.FindProject(context.Solution, message.ProjectId) is not { } project)
            return new ProjectTestsResponse(false, "", []);

        var version = (await project.GetDependentVersionAsync(cancellationToken)).ToString();
        if (Known.TryGetValue(project.Id, out var known) && known.Version == version)
            return known;
        if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
            return new ProjectTestsResponse(true, version, []);

        var tests = new List<TestMethodEntry>();
        foreach (var type in TypesIn(compilation.Assembly.GlobalNamespace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CanRun(type))
                tests.AddRange(TestsOf(type));
        }

        var response = new ProjectTestsResponse(true, version, tests);
        Known[project.Id] = response;
        return response;
    }

    // ------------------------------------------------------------ which methods are tests

    /// <summary>The attributes that make a method a test, by namespace and name; ones derived from these count too
    /// (xUnit's [Theory] is a [Fact], MSTest's [DataTestMethod] a [TestMethod], and a project's own).</summary>
    private static readonly HashSet<string> TestAttributes =
    [
        "Xunit.FactAttribute",
        "NUnit.Framework.TestAttribute",
        "NUnit.Framework.TestCaseAttribute",
        "NUnit.Framework.TestCaseSourceAttribute",
        "NUnit.Framework.TheoryAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute",
        "TUnit.Core.TestAttribute"
    ];

    /// <summary>Attributes that make a test run once per data row, though the method may have no parameters.</summary>
    private static readonly HashSet<string> DataAttributes =
    [
        "Xunit.TheoryAttribute",
        "NUnit.Framework.TestCaseAttribute",
        "NUnit.Framework.TestCaseSourceAttribute",
        "NUnit.Framework.TheoryAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethodAttribute"
    ];

    private static IEnumerable<INamedTypeSymbol> TypesIn(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
            foreach (var nested in WithNested(type))
                yield return nested;
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in TypesIn(child))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var inner in WithNested(nested))
                yield return inner;
    }

    /// <summary>A class the frameworks can make and run: not abstract (a static class is fine) and not an open generic.</summary>
    private static bool CanRun(INamedTypeSymbol type)
        => type.TypeKind == TypeKind.Class
           && (!type.IsAbstract || type.IsStatic)
           && !type.IsGenericType;

    /// <summary>The class's tests, its base classes' included (the frameworks run those as the class's own); an
    /// override counts once, as the class's.</summary>
    private static IEnumerable<TestMethodEntry> TestsOf(INamedTypeSymbol type)
    {
        var className = FrameworkName(type);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.MethodKind != MethodKind.Ordinary || !seen.Add(method.Name))
                    continue;
                var attributes = method.GetAttributes().Select(attribute => attribute.AttributeClass).OfType<INamedTypeSymbol>().ToList();
                if (!attributes.Any(attribute => IsOrDerivesFrom(attribute, TestAttributes)))
                    continue;

                var dataDriven = method.Parameters.Length > 0 || attributes.Any(attribute => IsOrDerivesFrom(attribute, DataAttributes));
                var location = method.Locations.FirstOrDefault(location => location.IsInSource);
                var start = location?.GetLineSpan().StartLinePosition;
                yield return new TestMethodEntry(className, method.Name, dataDriven,
                    location?.SourceTree?.FilePath, start?.Line ?? 0, start?.Character ?? 0);
            }
        }
    }

    private static bool IsOrDerivesFrom(INamedTypeSymbol attribute, HashSet<string> names)
    {
        for (var current = attribute; current is not null; current = current.BaseType)
            if (names.Contains($"{current.ContainingNamespace.ToDisplayString()}.{current.Name}"))
                return true;
        return false;
    }

    /// <summary>"Ns.Class", a nested class "Ns.Outer+Inner": the name the test frameworks report.</summary>
    private static string FrameworkName(INamedTypeSymbol type)
    {
        var name = type.Name;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            name = $"{outer.Name}+{name}";
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? $"{ns.ToDisplayString()}.{name}" : name;
    }
}
