using System;
using System.IO;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Tests;

/// <summary>The Properties panel's reads and writes, through the real MSBuild host on a throwaway SDK project.</summary>
public sealed class ProjectItemPropertiesTests : IDisposable
{
    private const string OriginalProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <!-- keep this comment -->
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private readonly TempWorkspace _workspace = new();
    private readonly MsBuildHostClient _host = new(HostPath());
    private readonly string _project;

    public ProjectItemPropertiesTests()
    {
        _project = _workspace.Write("App/App.csproj", OriginalProject);
        _workspace.Write("App/Program.cs", "class Program { static void Main() { } }");
        _workspace.Write("App/notes.txt", "hello");
    }

    public void Dispose()
    {
        _host.Dispose();
        _workspace.Dispose();
    }

    private string File(string name) => Path.Combine(_workspace.Root, "App", name);
    private string ProjectText => System.IO.File.ReadAllText(_project);

    [Fact]
    public void Reads_the_build_action_of_globbed_files()
    {
        var program = _host.GetItemProperties(_project, File("Program.cs"));
        Assert.Equal("Compile", program.ItemType);
        Assert.Contains("AvaloniaResource", program.AvailableItemTypes);
        Assert.Equal("", program.MetadataOrEmpty("CopyToOutputDirectory"));

        Assert.Equal("None", _host.GetItemProperties(_project, File("notes.txt")).ItemType);
    }

    [Fact]
    public void Metadata_on_a_globbed_file_goes_on_an_Update_element_and_clearing_it_removes_it()
    {
        var result = _host.SetItemMetadata(_project, File("notes.txt"), "CopyToOutputDirectory", "PreserveNewest");

        Assert.Equal("PreserveNewest", result.MetadataOrEmpty("CopyToOutputDirectory"));
        Assert.Contains("<None Update=\"notes.txt\">", ProjectText);
        Assert.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", ProjectText);
        Assert.Contains("<!-- keep this comment -->", ProjectText);

        _host.SetItemMetadata(_project, File("notes.txt"), "CopyToOutputDirectory", "");
        Assert.DoesNotContain("notes.txt", ProjectText);
    }

    [Fact]
    public void Changing_the_build_action_of_a_globbed_file_and_back_leaves_the_project_as_it_was()
    {
        var result = _host.SetItemType(_project, File("Program.cs"), "None");

        Assert.Equal("None", result.ItemType);
        Assert.Contains("<Compile Remove=\"Program.cs\" />", ProjectText);
        Assert.Contains("<None Include=\"Program.cs\" />", ProjectText);

        Assert.Equal("Compile", _host.SetItemType(_project, File("Program.cs"), "Compile").ItemType);
        Assert.DoesNotContain("Program.cs", ProjectText);
    }

    [Fact]
    public void Metadata_moves_with_the_file_to_its_new_build_action()
    {
        _host.SetItemMetadata(_project, File("notes.txt"), "CopyToOutputDirectory", "Always");

        var result = _host.SetItemType(_project, File("notes.txt"), "EmbeddedResource");

        Assert.Equal("EmbeddedResource", result.ItemType);
        Assert.Equal("Always", result.MetadataOrEmpty("CopyToOutputDirectory"));
        Assert.DoesNotContain("<None Update", ProjectText);
    }

    /// <summary>The host built next to this project: Projects/AXamlIDE.msbuildhost/bin/&lt;config&gt;/&lt;tfm&gt;/.</summary>
    private static string HostPath()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var (framework, configuration) = (testOutput.Name, testOutput.Parent!.Name);
        var projects = testOutput.Parent!.Parent!.Parent!.Parent!.FullName;
        var name = OperatingSystem.IsWindows() ? "AXamlIDE.msbuildhost.exe" : "AXamlIDE.msbuildhost";
        return Path.Combine(projects, "AXamlIDE.msbuildhost", "bin", configuration, framework, name);
    }
}
