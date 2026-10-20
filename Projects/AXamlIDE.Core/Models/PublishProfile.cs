namespace AXamlIDE.Models;

/// <summary>
/// One publish profile: a Visual Studio .pubxml in the project's Properties/PublishProfiles folder, so VS, Rider and
/// <c>dotnet publish -p:PublishProfile=Name</c> all read the same file. Only the settings the Publish page shows are
/// here; PublishProfileStore keeps everything else in the file as it was.
/// </summary>
/// <param name="Name">The file name without .pubxml (what PublishProfile= takes).</param>
/// <param name="FilePath">The .pubxml file; it may not exist yet (a new profile).</param>
/// <param name="TargetId">Where it publishes to: <c>_TargetId</c> as VS writes it (Folder, ClickOnce, AzureWebApp...).</param>
/// <param name="PublishDir">The target location as written, relative to the project folder or absolute.</param>
/// <param name="TargetFramework">null = the project's (only) framework.</param>
/// <param name="RuntimeIdentifier">null = Portable (any runtime, framework-dependent).</param>
public sealed record PublishProfile(
    string Name,
    string FilePath,
    string TargetId,
    string PublishDir,
    string Configuration = "Release",
    string? TargetFramework = null,
    string? RuntimeIdentifier = null,
    bool SelfContained = false,
    bool DeleteExistingFiles = false,
    bool PublishSingleFile = false,
    bool PublishReadyToRun = false,
    bool PublishTrimmed = false)
{
    public bool IsPortable => RuntimeIdentifier is null;

    public override string ToString() => Name;
}
