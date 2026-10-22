using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AXamlIDE.Services.Workspace;

/// <summary>A package restore resolved, and the packages it depends on (by id).</summary>
public sealed record ResolvedPackage(string Version, IReadOnlyList<string> Dependencies);

/// <summary>What restore resolved for one target framework.</summary>
/// <param name="TopLevelPackages">The PackageReferences, without the SDK's own (ILLink...), which VS hides too.</param>
/// <param name="Packages">Every package restore resolved, top-level and transitive, by id.</param>
public sealed record AssetsTarget(
    string Alias,
    IReadOnlyList<string> FrameworkReferences,
    IReadOnlyList<(string Name, string? Version)> TopLevelPackages,
    IReadOnlyDictionary<string, ResolvedPackage> Packages,
    IReadOnlyList<(string Name, string Path)> PackageAnalyzers)
{
    /// <summary>
    /// The packages this target only gets through other packages, each with the top-level packages that bring it
    /// in (directly or further down), as Visual Studio's "Transitively referenced by".
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> TransitivePackages()
    {
        var topLevel = TopLevelPackages.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referencedBy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in topLevel)
        {
            // Dependency graphs share a lot (System.* everywhere), so each walk visits a package once.
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            var pending = new Stack<string>(Packages.GetValueOrDefault(root)?.Dependencies ?? []);
            while (pending.TryPop(out var id))
            {
                if (!visited.Add(id))
                    continue;
                if (!topLevel.Contains(id))
                {
                    if (!referencedBy.TryGetValue(id, out var roots))
                        referencedBy[id] = roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    roots.Add(root);
                }
                foreach (var dependency in Packages.GetValueOrDefault(id)?.Dependencies ?? [])
                    pending.Push(dependency);
            }
        }

        return referencedBy
            .Where(pair => Packages.ContainsKey(pair.Key))   // a dependency restore didn't resolve as a package
            .ToDictionary(pair => pair.Key, IReadOnlySet<string> (pair) => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A project's NuGet restore output (obj/project.assets.json): per target framework, the packages it resolved,
/// what they depend on, and their analyzers. Used by the explorer's Dependencies node and the NuGet manager.
/// </summary>
public sealed record ProjectAssets(IReadOnlyList<AssetsTarget> Targets)
{
    /// <param name="assetsPath">MSBuild's ProjectAssetsFile, when known; otherwise obj/project.assets.json.</param>
    /// <returns>Null when the project hasn't been restored (or the file can't be read just now).</returns>
    public static ProjectAssets? TryLoad(string csprojPath, string? assetsPath = null)
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
            // Half-written by a restore, or a format we don't know.
            return null;
        }
    }

    public static ProjectAssets Parse(JsonElement root)
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

        return new ProjectAssets(result);
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
