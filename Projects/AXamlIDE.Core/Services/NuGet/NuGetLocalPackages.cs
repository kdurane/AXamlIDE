using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace AXamlIDE.Services.NuGet;

/// <summary>
/// The global packages folder (~/.nuget/packages): every package restore has downloaded, laid out as
/// <c>id/version/id.nuspec</c> (all lower case). Read for details and icons without going online.
/// </summary>
public sealed class NuGetLocalPackages(string folder)
{
    public string Folder => folder;

    private string PackageFolder(string id, string version)
        => Path.Combine(folder, id.ToLowerInvariant(), version.ToLowerInvariant());

    /// <summary>A downloaded version's details from its .nuspec, or null if it isn't in the folder.</summary>
    public NuGetPackageDetails? TryGetDetails(string id, string version)
    {
        var packageFolder = PackageFolder(id, version);
        var nuspec = Path.Combine(packageFolder, id.ToLowerInvariant() + ".nuspec");
        if (!File.Exists(nuspec))
            return null;

        XDocument document;
        try
        {
            document = XDocument.Load(nuspec);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }

        var metadata = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metadata is null)
            return null;
        string? Value(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        var license = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
        var licenseExpression = license?.Attribute("type")?.Value == "expression" ? license.Value.Trim() : null;

        var groups = new List<NuGetDependencyGroup>();
        var dependencies = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencies");
        if (dependencies is not null)
        {
            var groupElements = dependencies.Elements().Where(e => e.Name.LocalName == "group").ToList();
            foreach (var group in groupElements)
                groups.Add(new NuGetDependencyGroup(group.Attribute("targetFramework")?.Value ?? "Any", Dependencies(group)));
            if (groupElements.Count == 0 && dependencies.Elements().Any())
                groups.Add(new NuGetDependencyGroup("Any", Dependencies(dependencies)));
        }

        var icon = Value("icon");
        var localIcon = icon is null ? null : Path.Combine(packageFolder, icon.Replace('\\', Path.DirectorySeparatorChar));

        return new NuGetPackageDetails(
            Value("id") ?? id,
            Value("version") ?? version,
            Value("title"),
            Value("description"),
            Value("authors"),
            licenseExpression,
            Value("licenseUrl"),
            Value("projectUrl"),
            Published: null,
            Value("tags")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [],
            groups,
            DeprecationMessage: null,
            Vulnerabilities: [],
            Value("iconUrl"),
            localIcon is not null && File.Exists(localIcon) ? localIcon : null);

        static List<NuGetDependency> Dependencies(XElement parent)
            => [.. parent.Elements().Where(e => e.Name.LocalName == "dependency")
                .Select(e => new NuGetDependency(e.Attribute("id")?.Value ?? "", e.Attribute("version")?.Value))];
    }

    /// <summary>The icon file a downloaded version ships, or null.</summary>
    public string? TryGetIconPath(string id, string version) => TryGetDetails(id, version)?.LocalIconPath;
}
