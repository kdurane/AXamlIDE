using System;
using System.Collections.Generic;

namespace AXamlIDE.Services.NuGet;

/// <summary>A package source from the user's NuGet configuration (nuget.config files, as dotnet sees them).</summary>
public sealed record NuGetSource(string Name, string Url, bool IsEnabled)
{
    /// <summary>A local folder of .nupkg files rather than a V3 feed (not searchable here).</summary>
    public bool IsLocalFolder => !Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                 && !Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One search result (Browse).</summary>
public sealed record NuGetSearchResult(
    string Id,
    string Version,
    string? Description,
    string? Authors,
    long? TotalDownloads,
    string? IconUrl,
    bool IsVerified,
    string SourceName);

/// <summary>One package the project references directly (from <c>dotnet list package</c>).</summary>
/// <param name="RequestedVersion">What the project asks for ("1.2.3", "[1.0,2.0)", or from Directory.Packages.props).</param>
/// <param name="ResolvedVersion">What restore picked.</param>
/// <param name="Frameworks">The target frameworks that reference it.</param>
public sealed record InstalledPackage(string Id, string? RequestedVersion, string? ResolvedVersion, IReadOnlyList<string> Frameworks);

/// <summary>What <c>dotnet list package</c> found, or why it couldn't.</summary>
public sealed record InstalledPackages(IReadOnlyList<InstalledPackage> Packages, string? Problem);

public sealed record NuGetDependency(string Id, string? Range);

public sealed record NuGetDependencyGroup(string TargetFramework, IReadOnlyList<NuGetDependency> Dependencies);

/// <summary>One version's details (the right-hand pane): from the feed's registration, or the local nuspec.</summary>
public sealed record NuGetPackageDetails(
    string Id,
    string Version,
    string? Title,
    string? Description,
    string? Authors,
    string? LicenseExpression,
    string? LicenseUrl,
    string? ProjectUrl,
    DateTimeOffset? Published,
    IReadOnlyList<string> Tags,
    IReadOnlyList<NuGetDependencyGroup> DependencyGroups,
    string? DeprecationMessage,
    IReadOnlyList<string> Vulnerabilities,
    string? IconUrl,
    string? LocalIconPath);

/// <summary>The result of a dotnet command that changes the project: its output, and whether it worked.</summary>
public sealed record NuGetChangeResult(bool Succeeded, string Output);
