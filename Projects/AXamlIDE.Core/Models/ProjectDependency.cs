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

/// <summary>
/// A project's evaluated properties, all from one MSBuild evaluation. Ask for this once rather than one property
/// at a time: each separate question would be a whole evaluation of its own.
/// </summary>
/// <param name="OutputPath">Relative to the project's folder (usually), with this OS's separators.</param>
/// <param name="AssetsFile">NuGet's project.assets.json (the path is set even before the first restore).</param>
/// <param name="References">The projects it references (ProjectReference), as full paths.</param>
/// <param name="TargetFrameworks">net10.0..., one or several; empty if it declares none.</param>
public sealed record ProjectFacts(
    string AssemblyName,
    string? OutputType,
    string? OutputPath,
    string? AssetsFile,
    IReadOnlyList<string> References,
    IReadOnlyList<string> TargetFrameworks);
