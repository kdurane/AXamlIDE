// This must run before anything else in the process touches a
// Microsoft.Build.* type. Being the very first statement in a dedicated
// Program.cs, in a project that references nothing else heavy, is what
// makes that guarantee easy to keep — there's nothing else here that
// could JIT one of those types first.

using System.Text.Json;
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
            : HandleRequest(request.ProjectPath);
    }
    catch (Exception ex)
    {
        responseJson = JsonSerializer.Serialize(new HostResponse(false, [], [], ex.Message, null, null, null, null));
    }
 
    Console.WriteLine(responseJson);
}

return;

static string HandleRequest(string projectPath)
{
    try
    {
        using var collection = new ProjectCollection();
        var project = Project.FromFile(projectPath,
            new ProjectOptions { ProjectCollection = collection });
 
        var files = project.Items
            .Where(i => i.ItemType is "Compile" or "AvaloniaXaml" or "AvaloniaResource"
                or "Content" or "None" or "EmbeddedResource" or "AdditionalFiles"
                or "Page" or "Resource" or "ApplicationDefinition")
            .Select(i => i.GetMetadataValue("FullPath"))
            .Where(File.Exists)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
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
        return JsonSerializer.Serialize(new HostResponse(
            true, files, projectReferences, null, assemblyName, outputType, outputPath, projectAssetsFile));
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new HostResponse(false, [], [], ex.Message, null, null, null, null));
    }
}

record HostRequest(string ProjectPath);

record HostResponse(
    bool Success,
    string[] Files,
    string[] ProjectReferences,
    string? Error,
    string? AssemblyName,
    string? OutputType,
    string? OutputPath,
    string? ProjectAssetsFile);