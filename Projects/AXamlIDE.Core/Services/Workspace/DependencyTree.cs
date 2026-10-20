using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        var assets = AssetsFile.TryLoad(csprojPath, contents.AssetsFile);
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

    // ------------------------------------------------------------- project.assets.json

    private sealed record ResolvedPackage(string Version, IReadOnlyList<string> Dependencies);

    private sealed record AssetsTarget(
        string Alias,
        IReadOnlyList<string> FrameworkReferences,
        IReadOnlyList<(string Name, string? Version)> TopLevelPackages,
        IReadOnlyDictionary<string, ResolvedPackage> Packages,
        IReadOnlyList<(string Name, string Path)> PackageAnalyzers);

    private sealed record AssetsFile(IReadOnlyList<AssetsTarget> Targets)
    {
        public static AssetsFile? TryLoad(string csprojPath, string? assetsPath)
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
            assetsPath = string.IsNullOrWhiteSpace(assetsPath)
                ? Path.Combine(projectDirectory, "obj", "project.assets.json")
                : Path.GetFullPath(assetsPath, projectDirectory);
            if (!File.Exists(assetsPath))
                return null;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
                return Parse(document.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or KeyNotFoundException)
            {
                // Half-written by a restore, or a format we don't know: show the declared dependencies only.
                return null;
            }
        }

        private static AssetsFile Parse(JsonElement root)
        {
            var packageFolder = root.TryGetProperty("packageFolders", out var folders)
                ? folders.EnumerateObject().Select(folder => folder.Name).FirstOrDefault()
                : null;
            var libraries = root.GetProperty("libraries");
            var targets = root.GetProperty("targets");
            var frameworks = root.GetProperty("project").GetProperty("frameworks").EnumerateObject().ToList();

            // Targets are keyed by framework (plus "/rid" for RID-specific ones, which we skip);
            // NuGet writes them in the same order as project.frameworks.
            var targetElements = targets.EnumerateObject().Where(target => !target.Name.Contains('/')).ToList();

            var result = new List<AssetsTarget>();
            for (var i = 0; i < frameworks.Count; i++)
            {
                var framework = frameworks[i];
                var target = targetElements.FirstOrDefault(t => t.Name.Equals(framework.Name, StringComparison.OrdinalIgnoreCase));
                if (target.Value.ValueKind != JsonValueKind.Object && i < targetElements.Count)
                    target = targetElements[i];

                result.Add(Target(framework, target.Value, libraries, packageFolder));
            }

            return new AssetsFile(result);
        }

        private static AssetsTarget Target(JsonProperty framework, JsonElement target, JsonElement libraries, string? packageFolder)
        {
            var frameworkReferences = framework.Value.TryGetProperty("frameworkReferences", out var references)
                ? references.EnumerateObject().Select(reference => reference.Name).ToList()
                : [];

            var topLevel = framework.Value.TryGetProperty("dependencies", out var dependencies)
                ? dependencies.EnumerateObject()
                    .Where(d => !IsAutoReferenced(d.Value))   // the SDK's own (ILLink...), which VS hides too
                    .Select(d => (d.Name, Version: d.Value.TryGetProperty("version", out var v) ? v.GetString() : null))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];

            var packages = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
            var analyzers = new List<(string Name, string Path)>();
            if (target.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in target.EnumerateObject())
                {
                    if (!entry.Value.TryGetProperty("type", out var type) || type.GetString() != "package")
                        continue;

                    var slash = entry.Name.IndexOf('/');
                    if (slash < 0)
                        continue;
                    var name = entry.Name[..slash];
                    var packageDependencies = entry.Value.TryGetProperty("dependencies", out var deps)
                        ? deps.EnumerateObject().Select(d => d.Name).ToList()
                        : [];
                    packages[name] = new ResolvedPackage(entry.Name[(slash + 1)..], packageDependencies);

                    if (libraries.TryGetProperty(entry.Name, out var library))
                        analyzers.AddRange(Analyzers(library, packageFolder));
                }
            }

            return new AssetsTarget(framework.Name, frameworkReferences, topLevel, packages, analyzers);
        }

        private static bool IsAutoReferenced(JsonElement dependency)
            => dependency.TryGetProperty("autoReferenced", out var auto) && auto.ValueKind == JsonValueKind.True;

        /// <summary>The package's C# (or language-neutral) analyzers: analyzers/dotnet[/roslynX.Y][/cs]/*.dll.</summary>
        private static IEnumerable<(string Name, string Path)> Analyzers(JsonElement library, string? packageFolder)
        {
            if (!library.TryGetProperty("files", out var files) || !library.TryGetProperty("path", out var libraryPath))
                yield break;

            foreach (var file in files.EnumerateArray().Select(f => f.GetString() ?? ""))
            {
                if (!file.StartsWith("analyzers/dotnet/", StringComparison.OrdinalIgnoreCase)
                    || !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                var folders = file.Split('/')[2..^1];
                if (folders.Any(folder => folder is "vb" or "fs") || folders.Count(folder => !folder.StartsWith("roslyn")) > 1)
                    continue;

                var fullPath = packageFolder is null
                    ? ""
                    : Path.Combine(packageFolder, libraryPath.GetString() ?? "", file.Replace('/', Path.DirectorySeparatorChar));
                yield return (Path.GetFileNameWithoutExtension(file), fullPath);
            }
        }
    }
}
