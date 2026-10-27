using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace AXamlIDE.Services.Testing;

/// <summary>
/// Which projects hold tests, read from the project file the way Visual Studio decides: &lt;IsTestProject&gt;true,
/// the MSTest SDK, or a reference to the test SDK or a test framework.
/// </summary>
public static class TestProjectDetector
{
    private static readonly HashSet<string> TestPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "MSTest", "MSTest.TestFramework", "TUnit"
    };

    public static IReadOnlyList<string> FindTestProjects(IEnumerable<string> projectPaths)
        => [.. projectPaths.Where(IsTestProjectFile)];

    public static bool IsTestProjectFile(string projectPath)
    {
        try
        {
            return File.Exists(projectPath) && IsTestProject(File.ReadAllText(projectPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True for a project file's text that makes a test project.</summary>
    public static bool IsTestProject(string projectXml)
    {
        XDocument project;
        try
        {
            project = XDocument.Parse(projectXml);
        }
        catch (XmlException)
        {
            return false;
        }

        if (project.Root is not { } root)
            return false;
        if (((string?)root.Attribute("Sdk"))?.Contains("MSTest.Sdk", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        foreach (var element in root.Descendants())
        {
            if (element.Name.LocalName == "IsTestProject")
                return string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        return root.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"))
            .Any(id => id is not null && TestPackages.Contains(id));
    }
}
