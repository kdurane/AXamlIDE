using System;
using System.Collections.Generic;
using System.Linq;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Workspace;

/// <summary>
/// A project's Dependencies node, laid out as Visual Studio's Solution Explorer does:
///
///   Dependencies
///     [net8.0, net10.0 ...]      only when the project targets more than one framework
///       Frameworks               Microsoft.NETCore.App ...
///       Packages                 Avalonia (12.1.4) > its own dependencies > ...
///       Projects                 ProjectReferences
///       Analyzers                the SDK's and the packages' analyzers / source generators
///       Assemblies               plain References
///
/// The project file (evaluated) says what's declared; NuGet's restore output (project.assets.json) adds what
/// they resolved to: versions, transitive packages and package analyzers. Without a restore, only the declared
/// entries show. Empty categories are left out.
/// </summary>
public static class DependencyTree
{
    private const string DependenciesIcon = "Icon.VectorLink";
    private const string FrameworkIcon = "Icon.Cube";
    private const string PackageIcon = "Icon.PackageVariantClosed";
    private const string ProjectIcon = "Icon.FolderMultipleOutline";
    private const string AnalyzerIcon = "Icon.ShieldCheckOutline";
    private const string AssemblyIcon = "Icon.Cube";

    public static ProjectTreeNode Create(string csprojPath, ProjectContents contents)
    {
        var assets = ProjectAssets.TryLoad(csprojPath, contents.AssetsFile);
        var targets = assets?.Targets ?? [];

        var children = targets.Count > 1
            ? targets.Select(target => Group(target.Alias, FrameworkIcon, Categories(contents.Dependencies, target)))
            : Categories(contents.Dependencies, targets.FirstOrDefault());
        return Group("Dependencies", DependenciesIcon, children);
    }

    private static IEnumerable<ProjectTreeNode> Categories(IReadOnlyList<ProjectDependency> declared, AssetsTarget? target)
    {
        var frameworks = (target?.FrameworkReferences ?? [])
            .Concat(Declared(declared, DependencyKind.Framework).Select(d => d.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => Leaf(name, FrameworkIcon, ""));

        var packages = target is { TopLevelPackages.Count: > 0 }
            ? target.TopLevelPackages.Select(p => Package(target, p.Name, p.Version))
            : Declared(declared, DependencyKind.Package).Select(d => Leaf(WithVersion(d.Name, d.Version), PackageIcon, ""));

        var projects = Declared(declared, DependencyKind.Project).Select(d => Leaf(d.Name, ProjectIcon, d.Path ?? ""));

        var analyzers = Declared(declared, DependencyKind.Analyzer).Select(d => (d.Name, Path: d.Path ?? ""))
            .Concat(target?.PackageAnalyzers ?? [])
            .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => Leaf(a.Name, AnalyzerIcon, a.Path));

        var assemblies = Declared(declared, DependencyKind.Assembly).Select(d => Leaf(d.Name, AssemblyIcon, d.Path ?? ""));

        return new[]
            {
                ("Frameworks", FrameworkIcon, frameworks.ToList()),
                ("Packages", PackageIcon, packages.ToList()),
                ("Projects", ProjectIcon, projects.ToList()),
                ("Analyzers", AnalyzerIcon, analyzers.ToList()),
                ("Assemblies", AssemblyIcon, assemblies.ToList())
            }
            .Where(category => category.Item3.Count > 0)
            .Select(category => Group(category.Item1, category.Item2, category.Item3));
    }

    private static IEnumerable<ProjectDependency> Declared(IEnumerable<ProjectDependency> declared, DependencyKind kind)
        => declared.Where(d => d.Kind == kind).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A package and, when expanded, the packages it depends on (resolved versions; loaded lazily).</summary>
    private static ProjectTreeNode Package(AssetsTarget target, string name, string? requestedVersion)
    {
        var resolved = target.Packages.GetValueOrDefault(name);
        var label = WithVersion(name, resolved?.Version ?? requestedVersion);
        if (resolved is not { Dependencies.Count: > 0 })
            return Leaf(label, PackageIcon, "");

        return ProjectTreeNode.CreateDependency(label, PackageIcon, "",
            () => resolved.Dependencies.Order(StringComparer.OrdinalIgnoreCase).Select(dependency => Package(target, dependency, null)));
    }

    private static string WithVersion(string name, string? version)
        => string.IsNullOrEmpty(version) ? name : $"{name} ({version})";

    private static ProjectTreeNode Group(string name, string icon, IEnumerable<ProjectTreeNode> children)
    {
        var childList = children.ToList();
        var node = ProjectTreeNode.CreateDependency(name, icon, "", () => childList);
        node.Populate();
        return node;
    }

    private static ProjectTreeNode Leaf(string name, string icon, string path)
        => ProjectTreeNode.CreateDependency(name, icon, path, null);
}
