using System;
using System.Collections.Generic;
using System.IO;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Publishing;

/// <summary>
/// How a profile is published: the <c>dotnet</c> command line, and the folder it ends up in.
///
/// A folder profile is <c>dotnet publish project -p:PublishProfile=Name</c>: the SDK reads the .pubxml itself, so
/// the files VS writes behave the same here. Configuration and framework are passed too, because they pick what
/// gets built before the profile is read. One thing the SDK doesn't do for a plain (non-web) project is VS's
/// "Delete all existing files prior to publish"; <see cref="ClearFolder"/> does that.
/// </summary>
public static class PublishCommandLine
{
    /// <summary>The arguments after <c>dotnet</c>; throws for a profile whose target AXamlIDE can't publish.</summary>
    public static IReadOnlyList<string> Arguments(string projectPath, PublishProfile profile)
    {
        if (!PublishTargets.CanPublish(profile))
            throw new NotSupportedException($"AXamlIDE can't publish to {PublishTargets.DisplayName(profile.TargetId)}.");

        List<string> arguments = ["publish", projectPath, "-p:PublishProfile=" + profile.Name, "--configuration", profile.Configuration];
        if (profile.TargetFramework is { } framework)
            arguments.AddRange(["--framework", framework]);
        return arguments;
    }

    /// <summary>
    /// The profile's target location as a full path: relative to the project folder, with the few MSBuild
    /// properties VS puts in it ($(Configuration)...) filled in, and Windows separators turned round elsewhere.
    /// </summary>
    public static string PublishFolder(string projectPath, PublishProfile profile)
    {
        var projectFolder = Path.GetDirectoryName(projectPath)!;
        var folder = profile.PublishDir.Length > 0
            ? profile.PublishDir
            : PublishProfileStore.DefaultLocation(profile.TargetFramework);
        folder = folder
            .Replace("$(Configuration)", profile.Configuration, StringComparison.OrdinalIgnoreCase)
            .Replace("$(TargetFramework)", profile.TargetFramework ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("$(RuntimeIdentifier)", profile.RuntimeIdentifier ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildProjectDirectory)", projectFolder, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(profile.FilePath) + "/", StringComparison.OrdinalIgnoreCase);
        if (Path.DirectorySeparatorChar != '\\')
            folder = folder.Replace('\\', '/');
        return Path.GetFullPath(Path.Combine(projectFolder, folder));
    }

    /// <summary>
    /// Why <paramref name="folder"/> mustn't be emptied before publishing, or null if it can be. Emptying a drive,
    /// the home folder, or the project (or a folder above it) would be a disaster, so those are refused.
    /// </summary>
    public static string? WhyNotClear(string folder, string projectPath)
    {
        var full = Trim(Path.GetFullPath(folder));
        var projectFolder = Trim(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
        var home = Trim(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (Path.GetPathRoot(full) is { } root && string.Equals(Trim(root), full, comparison))
            return $"{full} is a drive's root folder.";
        if (home.Length > 0 && string.Equals(home, full, comparison))
            return $"{full} is your home folder.";
        if (string.Equals(projectFolder, full, comparison) || IsInside(projectFolder, full, comparison))
            return $"{full} holds the project itself.";
        return null;
    }

    /// <summary>Delete everything in <paramref name="folder"/> (the folder stays); check <see cref="WhyNotClear"/> first.</summary>
    public static void ClearFolder(string folder)
    {
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists)
            return;
        foreach (var file in directory.EnumerateFiles())
            file.Delete();
        foreach (var child in directory.EnumerateDirectories())
            child.Delete(recursive: true);
    }

    private static bool IsInside(string path, string folder, StringComparison comparison)
        => path.StartsWith(folder + Path.DirectorySeparatorChar, comparison)
           || folder == Path.DirectorySeparatorChar.ToString();

    private static string Trim(string path)
        => path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}
