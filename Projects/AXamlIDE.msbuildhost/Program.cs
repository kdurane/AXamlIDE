// This must run before anything else in the process touches a
// Microsoft.Build.* type. Being the very first statement in a dedicated
// Program.cs, in a project that references nothing else heavy, is what
// makes that guarantee easy to keep — there's nothing else here that
// could JIT one of those types first.

using System.Text.Json;
using AXamlIDE.MsBuildHost;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
// ReSharper disable ArrangeTypeModifiers
// ReSharper disable NotAccessedPositionalProperty.Global

MSBuildLocator.RegisterDefaults();

while (Console.ReadLine() is { } line)
{
    if (line.Length == 0) continue;
 
    string responseJson;
    try
    {
        var request = JsonSerializer.Deserialize<HostRequest>(line);
        responseJson = request is null
            ? JsonSerializer.Serialize(new HostResponse(false, [], [], "Malformed request.", null, null, null, null))
            : request.Command switch
            {
                HostCommands.GetItem => ItemResponse(() => ItemProperties.Get(request.ProjectPath, request.FilePath!)),
                HostCommands.SetItem => ItemResponse(() => ItemProperties.Set(request.ProjectPath, request.FilePath!,
                    request.ItemType, request.MetadataName, request.MetadataValue)),
                HostCommands.MoveItem => MoveResponse(request.ProjectPath, request.FilePath!, request.NewPath),
                _ => HandleRequest(request.ProjectPath, request.Configuration)
            };
    }
    catch (Exception ex)
    {
        responseJson = JsonSerializer.Serialize(new HostResponse(false, [], [], ex.Message, null, null, null, null));
    }
 
    Console.WriteLine(responseJson);
}

return;

static string HandleRequest(string projectPath, string? configuration)
{
    try
    {
        var globalProperties = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(configuration))
            globalProperties["Configuration"] = configuration;
        using var collection = new ProjectCollection(globalProperties);
        var project = Project.FromFile(projectPath,
            new ProjectOptions { ProjectCollection = collection });
 
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // Every build action a file can have: the usual ones plus whatever the project (or its SDKs) offers
        // as AvailableItemName - the same list VS's Properties window shows, so no file in the project is missed.
        var fileItemTypes = new HashSet<string>(
            ["Compile", "AvaloniaXaml", "AvaloniaResource", "Content", "None", "EmbeddedResource", "AdditionalFiles",
             "Page", "Resource", "ApplicationDefinition"]);
        fileItemTypes.UnionWith(project.GetItems("AvailableItemName")
            .Select(item => item.EvaluatedInclude)
            .Where(type => !type.StartsWith('_')));

        var files = project.Items
            .Where(i => fileItemTypes.Contains(i.ItemType))
            .Select(i => i.GetMetadataValue("FullPath"))
            .Where(File.Exists)
            .Distinct(pathComparer)
            .OrderBy(p => p)
            .ToArray();

        // <Folder Include="Assets/" /> keeps an empty folder in the project.
        var folders = project.GetItems("Folder")
            .Select(item => item.GetMetadataValue("FullPath").TrimEnd('/', '\\'))
            .Where(Directory.Exists)
            .Distinct(pathComparer)
            .OrderBy(p => p)
            .ToArray();

        var projectReferences = project.GetItems("ProjectReference")
            .Select(item => item.GetMetadataValue("FullPath"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path)
            .ToArray();

        var assemblyName = project.GetPropertyValue("AssemblyName");
        var outputType = project.GetPropertyValue("OutputType");
        var outputPath = project.GetPropertyValue("OutputPath");
        var projectAssetsFile = project.GetPropertyValue("ProjectAssetsFile");
        // A multi-targeted project lists them in TargetFrameworks (its outer evaluation has no TargetFramework).
        var targetFrameworks = (project.GetPropertyValue("TargetFrameworks") is { Length: > 0 } list
                ? list
                : project.GetPropertyValue("TargetFramework"))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return JsonSerializer.Serialize(new HostResponse(
            true, files, projectReferences, null, assemblyName, outputType, outputPath, projectAssetsFile,
            Dependencies: Dependencies(project).ToArray(), Folders: folders, TargetFrameworks: targetFrameworks));
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new HostResponse(false, [], [], ex.Message, null, null, null, null));
    }
}

/// <summary>What the project declares under VS's Dependencies node; NuGet's restore output adds the rest.</summary>
static IEnumerable<HostDependency> Dependencies(Project project)
{
    static bool IsImplicit(ProjectItem item) => item.GetMetadataValue("IsImplicitlyDefined") == "true";
    static string? NonEmpty(string value) => value.Length > 0 ? value : null;

    foreach (var item in project.GetItems("FrameworkReference"))
        yield return new HostDependency(HostDependencyKinds.Framework, item.EvaluatedInclude, null, null, IsImplicit(item));

    foreach (var item in project.GetItems("PackageReference"))
    {
        // Central package management puts the version on a PackageVersion item instead.
        var version = NonEmpty(item.GetMetadataValue("VersionOverride")) ?? NonEmpty(item.GetMetadataValue("Version"))
            ?? project.GetItems("PackageVersion").FirstOrDefault(v => v.EvaluatedInclude.Equals(item.EvaluatedInclude, StringComparison.OrdinalIgnoreCase))
                ?.GetMetadataValue("Version");
        yield return new HostDependency(HostDependencyKinds.Package, item.EvaluatedInclude, version, null, IsImplicit(item));
    }

    foreach (var item in project.GetItems("ProjectReference"))
    {
        var path = item.GetMetadataValue("FullPath");
        yield return new HostDependency(HostDependencyKinds.Project, Path.GetFileNameWithoutExtension(path), null, path, IsImplicit(item));
    }

    foreach (var item in project.GetItems("Analyzer"))
    {
        var path = item.GetMetadataValue("FullPath");
        yield return new HostDependency(HostDependencyKinds.Analyzer, Path.GetFileNameWithoutExtension(path), null, path, IsImplicit(item));
    }

    foreach (var item in project.GetItems("Reference").Where(item => !IsImplicit(item)))
    {
        // "System.Drawing, Version=4.0.0.0, ..." -> System.Drawing
        var name = item.EvaluatedInclude.Split(',')[0].Trim();
        var hintPath = NonEmpty(item.GetMetadataValue("HintPath")) is { } hint
            ? Path.GetFullPath(hint, project.DirectoryPath)
            : null;
        yield return new HostDependency(HostDependencyKinds.Assembly, name, null, hintPath, false);
    }
}

static string ItemResponse(Func<HostItem> read)
{
    try
    {
        return JsonSerializer.Serialize(new HostResponse(true, [], [], null, null, null, null, null, read()));
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new HostResponse(false, [], [], ex.Message, null, null, null, null));
    }
}

static string MoveResponse(string projectPath, string oldPath, string? newPath)
{
    try
    {
        var changed = ItemPaths.Move(projectPath, oldPath, newPath);
        return JsonSerializer.Serialize(new HostResponse(true, [], [], null, null, null, null, null, ProjectChanged: changed));
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new HostResponse(false, [], [], ex.Message, null, null, null, null));
    }
}
