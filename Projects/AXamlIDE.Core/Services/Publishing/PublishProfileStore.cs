using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Publishing;

/// <summary>
/// A project's publish profiles: the .pubxml files in Properties/PublishProfiles, the same files and folder Visual
/// Studio uses, so a profile made in one IDE works in the other and with <c>dotnet publish -p:PublishProfile=</c>.
///
/// Reading understands VS's folder profiles old and new (PublishDir / PublishUrl, _TargetId / PublishProtocol /
/// WebPublishMethod). Saving changes only the properties the Publish page edits; the rest of the file stays.
/// </summary>
public static class PublishProfileStore
{
    public const string Extension = ".pubxml";

    /// <summary>VS's .csproj.user property naming the profile last chosen on its Publish page.</summary>
    private const string LastSelectedProperty = "_LastSelectedProfileId";

    public static string FolderFor(string projectPath)
        => Path.Combine(Path.GetDirectoryName(projectPath)!, "Properties", "PublishProfiles");

    public static string PathFor(string projectPath, string name) => Path.Combine(FolderFor(projectPath), name + Extension);

    /// <summary>The project's profiles, by name; ones that can't be read are left out.</summary>
    public static IReadOnlyList<PublishProfile> Load(string projectPath)
    {
        var folder = FolderFor(projectPath);
        if (!Directory.Exists(folder))
            return [];

        var profiles = new List<PublishProfile>();
        foreach (var file in Directory.EnumerateFiles(folder, "*" + Extension))
        {
            try
            {
                profiles.Add(Read(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                // A broken file shouldn't hide the others.
            }
        }
        return [.. profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static PublishProfile Read(string path)
    {
        var properties = Properties(XDocument.Load(path));
        string? Get(string name) => properties.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
        bool Flag(string name) => string.Equals(Get(name), "true", StringComparison.OrdinalIgnoreCase);

        return new PublishProfile(
            Path.GetFileNameWithoutExtension(path),
            path,
            TargetIdOf(Get),
            Get("PublishDir") ?? Get("PublishUrl") ?? "",
            Get("Configuration") ?? "Release",
            Get("TargetFramework"),
            Get("RuntimeIdentifier"),
            Flag("SelfContained"),
            Flag("DeleteExistingFiles"),
            Flag("PublishSingleFile"),
            Flag("PublishReadyToRun"),
            Flag("PublishTrimmed"));
    }

    /// <summary>
    /// Write the profile to its file (creating it, and the folder, for a new one). Settings that don't apply are
    /// removed rather than left behind: single file and ReadyToRun need a runtime, trimming a self-contained app.
    /// </summary>
    public static void Save(PublishProfile profile)
    {
        var isNew = !File.Exists(profile.FilePath);
        var document = isNew ? NewDocument() : XDocument.Load(profile.FilePath);
        var specific = !profile.IsPortable;

        Set(document, "Configuration", profile.Configuration);
        if (isNew)
        {
            Set(document, "Platform", "Any CPU");
            Set(document, "PublishProtocol", PublishTargets.Find(profile.TargetId)?.Protocol);
        }
        Set(document, "PublishDir", profile.PublishDir);
        if (Has(document, "PublishUrl"))   // an older VS profile: keep both saying the same
            Set(document, "PublishUrl", profile.PublishDir);
        Set(document, "_TargetId", profile.TargetId);
        Set(document, "TargetFramework", profile.TargetFramework);
        Set(document, "RuntimeIdentifier", profile.RuntimeIdentifier);
        Set(document, "SelfContained", Bool(specific && profile.SelfContained));
        Set(document, "PublishSingleFile", specific ? Bool(profile.PublishSingleFile) : null);
        Set(document, "PublishReadyToRun", specific ? Bool(profile.PublishReadyToRun) : null);
        Set(document, "PublishTrimmed", specific && profile.SelfContained ? Bool(profile.PublishTrimmed) : null);
        Set(document, "DeleteExistingFiles", Bool(profile.DeleteExistingFiles));

        Directory.CreateDirectory(Path.GetDirectoryName(profile.FilePath)!);
        document.Save(profile.FilePath);
    }

    /// <summary>Rename the profile's file (and VS's .pubxml.user beside it); the renamed profile.</summary>
    public static PublishProfile Rename(PublishProfile profile, string newName)
    {
        var newPath = Path.Combine(Path.GetDirectoryName(profile.FilePath)!, newName + Extension);
        File.Move(profile.FilePath, newPath);
        if (File.Exists(profile.FilePath + ".user"))
            File.Move(profile.FilePath + ".user", newPath + ".user");
        return profile with { Name = newName, FilePath = newPath };
    }

    public static void Delete(PublishProfile profile)
    {
        File.Delete(profile.FilePath);
        File.Delete(profile.FilePath + ".user");   // no error if it isn't there
    }

    /// <summary>VS's Import Profile: copy a .pubxml into the project (renamed if the name's taken) and read it.</summary>
    public static PublishProfile Import(string projectPath, string sourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var target = PathFor(projectPath, name);
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.Ordinal))
            return Read(target);   // it's already one of the project's

        Read(sourcePath);   // throws if it isn't a profile, before copying it in
        target = PathFor(projectPath, UniqueName(projectPath, name));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourcePath, target);
        return Read(target);
    }

    /// <summary>A new profile for <paramref name="target"/>, named as VS does (FolderProfile, FolderProfile1...).</summary>
    public static PublishProfile NewProfile(string projectPath, PublishTarget target, string location, string? targetFramework)
    {
        var name = UniqueName(projectPath, target.Id + "Profile");
        return new PublishProfile(name, PathFor(projectPath, name), target.Id, location, TargetFramework: targetFramework);
    }

    /// <summary>VS's default folder: bin/Release/{framework}/publish/ under the project.</summary>
    public static string DefaultLocation(string? targetFramework)
        => targetFramework is null ? "bin/Release/publish/" : $"bin/Release/{targetFramework}/publish/";

    /// <summary>A folder picked with Browse, as a profile keeps it: relative inside the project (bin/...), else full.</summary>
    public static string LocationFor(string projectPath, string folder)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, folder);
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? folder
            : relative.Replace('\\', '/') + "/";
    }

    /// <summary><paramref name="baseName"/>, or with the first number after it that no profile has.</summary>
    public static string UniqueName(string projectPath, string baseName)
    {
        if (!File.Exists(PathFor(projectPath, baseName)))
            return baseName;
        for (var n = 1; ; n++)
            if (!File.Exists(PathFor(projectPath, baseName + n)))
                return baseName + n;
    }

    /// <summary>Why <paramref name="name"/> can't be a profile name, or null if it can.</summary>
    public static string? ValidateName(string projectPath, string name, PublishProfile? renaming = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name.";
        if (name != name.Trim() || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                                || name.IndexOfAny([';', ',', '$', '%', '@', '\'', '"', '\\', '/']) >= 0)
            return "A profile name can't have these characters: \\ / : * ? \" < > | ; , $ % @ '";
        if (renaming is not null && string.Equals(renaming.Name, name, StringComparison.Ordinal))
            return null;
        return File.Exists(PathFor(projectPath, name)) ? $"There's already a profile called {name}." : null;
    }

    /// <summary>
    /// The profile to show first: the one Visual Studio last had selected (its .csproj.user), else the one changed
    /// most recently.
    /// </summary>
    public static PublishProfile? Initial(string projectPath, IReadOnlyList<PublishProfile> profiles)
    {
        if (profiles.Count == 0)
            return null;
        if (LastSelectedInVisualStudio(projectPath) is { } last
            && profiles.FirstOrDefault(profile => string.Equals(profile.Name, last, StringComparison.OrdinalIgnoreCase)) is { } chosen)
            return chosen;
        return profiles.MaxBy(profile => File.GetLastWriteTimeUtc(profile.FilePath));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The profile name in VS's .csproj.user (it stores the .pubxml's Windows path), or null.</summary>
    private static string? LastSelectedInVisualStudio(string projectPath)
    {
        try
        {
            var userFile = projectPath + ".user";
            if (!File.Exists(userFile)
                || !Properties(XDocument.Load(userFile)).TryGetValue(LastSelectedProperty, out var path))
                return null;
            var fileName = path.Replace('\\', '/').Split('/').Last();
            return fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? fileName[..^Extension.Length] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    /// <summary>What the profile publishes to, from whichever property the IDE that wrote it used.</summary>
    private static string TargetIdOf(Func<string, string?> get)
    {
        if (get("_TargetId") is { } id)
            return id;
        return (get("PublishProtocol") ?? get("WebPublishMethod")) switch
        {
            { } method when method.Equals(PublishTargets.FileSystemProtocol, StringComparison.OrdinalIgnoreCase) => PublishTargets.FolderId,
            { } method => method,   // ClickOnce, MSDeploy, FTP...
            null => get("PublishDir") is not null ? PublishTargets.FolderId : ""
        };
    }

    private static Dictionary<string, string> Properties(XDocument document)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in PropertyElements(document))
            properties[element.Name.LocalName] = element.Value.Trim();   // a later one wins, as in MSBuild
        return properties;
    }

    private static IEnumerable<XElement> PropertyElements(XDocument document)
        => document.Root?.Elements().Where(group => group.Name.LocalName == "PropertyGroup").SelectMany(group => group.Elements()) ?? [];

    private static bool Has(XDocument document, string name) => PropertyElements(document).Any(e => e.Name.LocalName == name);

    /// <summary>Set a property (where it already is, else in the first PropertyGroup); null removes it.</summary>
    private static void Set(XDocument document, string name, string? value)
    {
        var existing = PropertyElements(document).Where(e => e.Name.LocalName == name).ToList();
        if (value is null)
        {
            existing.ForEach(element => element.Remove());
            return;
        }
        if (existing.Count > 0)
        {
            existing[^1].Value = value;
            return;
        }

        var root = document.Root!;
        var group = root.Elements().FirstOrDefault(e => e.Name.LocalName == "PropertyGroup");
        if (group is null)
            root.Add(group = new XElement(root.Name.Namespace + "PropertyGroup"));
        group.Add(new XElement(root.Name.Namespace + name, value));
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static XDocument NewDocument() => new(
        new XDeclaration("1.0", "utf-8", null),
        new XComment(" Publish profile (AXamlIDE / Visual Studio). https://go.microsoft.com/fwlink/?LinkID=208121 "),
        new XElement("Project", new XElement("PropertyGroup")));
}
