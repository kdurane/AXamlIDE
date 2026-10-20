using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Publishing;

/// <summary>
/// A place a project can be published to: one entry on the first page of the Publish wizard (Visual Studio's
/// "Where are you publishing today?").
/// </summary>
/// <param name="Id">Written to the profile as <c>_TargetId</c>, the way VS tells its profiles apart.</param>
/// <param name="Protocol">Written as <c>PublishProtocol</c> (FileSystem for a folder).</param>
/// <param name="Icon">An icon key from Styles/IconResources.axaml.</param>
/// <param name="LocationPrompt">The wizard's location page: what to ask for.</param>
public sealed record PublishTarget(
    string Id,
    string Name,
    string Description,
    string Protocol,
    string Icon,
    string LocationPrompt);

/// <summary>What the Publish wizard ended with: a new profile's target and location, or a .pubxml to import.</summary>
public sealed record PublishWizardResult(PublishTarget? Target, string? Location, string? ImportPath);

/// <summary>
/// The publish targets AXamlIDE can publish to, and the runtimes and deployment modes a profile can choose.
///
/// To add a target (FTP, Docker, a web server...): add it to <see cref="All"/>; give it its command line in
/// PublishCommandLine (a folder profile is a plain <c>dotnet publish</c>); and give it its own location page in the
/// wizard if a folder path isn't the right question. Profiles for targets that aren't here (made by Visual Studio,
/// say) still list on the Publish page, but can't be published from AXamlIDE.
/// </summary>
public static class PublishTargets
{
    public const string FolderId = "Folder";
    public const string FileSystemProtocol = "FileSystem";

    public static readonly PublishTarget Folder = new(
        FolderId,
        "Folder",
        "Publish your application to a local folder or file share.",
        FileSystemProtocol,
        "Icon.FolderOutline",
        "Folder location");

    /// <summary>The targets the wizard offers, in its order.</summary>
    public static IReadOnlyList<PublishTarget> All { get; } = [Folder];

    public static PublishTarget? Find(string targetId)
        => All.FirstOrDefault(target => string.Equals(target.Id, targetId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether AXamlIDE knows how to publish this profile.</summary>
    public static bool CanPublish(PublishProfile profile) => Find(profile.TargetId) is not null;

    /// <summary>The target's name on the Publish page; Visual Studio's names for the ones AXamlIDE can't publish.</summary>
    public static string DisplayName(string targetId) => Find(targetId)?.Name ?? targetId switch
    {
        "ClickOnce" => "ClickOnce",
        "AzureWebApp" or "AzureAppService" => "Azure App Service",
        "AzureFunctions" => "Azure Functions",
        "AzureContainerApps" => "Azure Container Apps",
        "ContainerRegistry" or "DockerHub" => "Docker Container Registry",
        "MSDeploy" or "IIS" => "Web Server (IIS)",
        "FTP" => "FTP/FTPS Server",
        "" => "Unknown",
        _ => targetId
    };

    // ---------------------------------------------------------------- runtimes

    /// <summary>The profile setting for "no runtime": framework-dependent, runs on any OS.</summary>
    public const string Portable = "Portable";

    /// <summary>Visual Studio's Target runtime list (Portable, then runtime identifiers).</summary>
    public static IReadOnlyList<string> Runtimes { get; } =
    [
        Portable,
        "win-x86", "win-x64", "win-arm64",
        "osx-x64", "osx-arm64",
        "linux-x64", "linux-arm", "linux-arm64",
        "linux-musl-x64", "linux-musl-arm64"
    ];

    /// <summary>This machine's runtime identifier (linux-x64...): the runtime a self-contained build defaults to.</summary>
    public static string CurrentRuntime => RuntimeInformation.RuntimeIdentifier;

    public const string FrameworkDependent = "Framework-dependent";
    public const string SelfContainedMode = "Self-contained";

    public static IReadOnlyList<string> DeploymentModes { get; } = [FrameworkDependent, SelfContainedMode];

    public static IReadOnlyList<string> Configurations { get; } = ["Debug", "Release"];
}
