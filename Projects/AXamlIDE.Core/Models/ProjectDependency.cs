using System.Collections.Generic;

namespace AXamlIDE.Models;

public enum DependencyKind { Framework, Package, Project, Analyzer, Assembly }

/// <summary>One entry a project declares under VS's Dependencies node (from evaluating the project file).</summary>
/// <param name="Version">Packages: the requested version (or range).</param>
/// <param name="Path">Projects, analyzers and assemblies: the file on disk, when known.</param>
/// <param name="IsImplicit">Added by the SDK rather than written in the project.</param>
public sealed record ProjectDependency(DependencyKind Kind, string Name, string? Version, string? Path, bool IsImplicit);

/// <summary>What the Solution Explorer shows under a project: its files, its explicitly included (possibly
/// empty) folders, its declared dependencies, and NuGet's restore output for the transitive ones.</summary>
public sealed record ProjectContents(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Folders,
    IReadOnlyList<ProjectDependency> Dependencies,
    string? AssetsFile);
