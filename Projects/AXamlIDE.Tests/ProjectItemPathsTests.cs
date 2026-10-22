using System;
using System.IO;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Tests;

/// <summary>Moving, renaming and deleting files keeps the project file's own paths right (the real MSBuild host).</summary>
public sealed class ProjectItemPathsTests : IDisposable
{
    private const string OriginalProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <!-- keep this comment -->
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ApplicationIcon>icon.ico</ApplicationIcon>
          </PropertyGroup>
          <ItemGroup>
            <AvaloniaResource Include="icon.ico">
              <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
            </AvaloniaResource>
            <AvaloniaResource Include="Assets/*.json" />
            <None Update="Assets\data.txt" CopyToOutputDirectory="Always" />
            <Folder Include="Assets/Empty/" />
            <Compile Remove="old.cs;icon.ico;keep.cs" />
          </ItemGroup>
        </Project>
        """;

    private readonly TempWorkspace _workspace = new();
    private readonly MsBuildHostClient _host = new(ProjectItemPropertiesTests.HostPath());
    private readonly string _project;

    public ProjectItemPathsTests()
    {
        _project = _workspace.Write("App/App.csproj", OriginalProject);
    }

    public void Dispose()
    {
        _host.Dispose();
        _workspace.Dispose();
    }

    private string PathOf(string relative) => Path.Combine(_workspace.Root, "App", relative);
    private string ProjectText => File.ReadAllText(_project);

    [Fact]
    public void Moving_a_file_rewrites_its_items_and_the_properties_that_name_it()
    {
        Assert.True(_host.MoveItem(_project, PathOf("icon.ico"), PathOf("Assets/AppIcon.ico")));

        Assert.Contains("<ApplicationIcon>Assets/AppIcon.ico</ApplicationIcon>", ProjectText);
        Assert.Contains("<AvaloniaResource Include=\"Assets/AppIcon.ico\">", ProjectText);
        Assert.Contains("<Compile Remove=\"old.cs;Assets/AppIcon.ico;keep.cs\" />", ProjectText);
        Assert.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", ProjectText);
        Assert.Contains("<!-- keep this comment -->", ProjectText);
    }

    [Fact]
    public void Renaming_a_folder_carries_the_paths_under_it_and_keeps_their_style()
    {
        Assert.True(_host.MoveItem(_project, PathOf("Assets"), PathOf("Content")));

        Assert.Contains("<None Update=\"Content\\data.txt\"", ProjectText);
        Assert.Contains("<Folder Include=\"Content/Empty/\" />", ProjectText);
        Assert.Contains("<AvaloniaResource Include=\"Assets/*.json\" />", ProjectText);   // a glob: left alone
    }

    [Fact]
    public void Deleting_a_file_drops_its_items_but_not_its_properties()
    {
        Assert.True(_host.MoveItem(_project, PathOf("icon.ico"), null));

        Assert.DoesNotContain("<AvaloniaResource Include=\"icon.ico\"", ProjectText);
        Assert.Contains("<Compile Remove=\"old.cs;keep.cs\" />", ProjectText);
        Assert.Contains("<ApplicationIcon>icon.ico</ApplicationIcon>", ProjectText);
    }

    [Fact]
    public void Moving_a_file_out_of_the_project_folder_is_a_delete_for_that_project()
    {
        Assert.True(_host.MoveItem(_project, PathOf("Assets/data.txt"), Path.Combine(_workspace.Root, "data.txt")));

        Assert.DoesNotContain("data.txt", ProjectText);
    }

    [Fact]
    public void A_file_the_project_doesnt_name_leaves_it_untouched()
    {
        Assert.False(_host.MoveItem(_project, PathOf("Program.cs"), PathOf("Main.cs")));

        Assert.Equal(OriginalProject, ProjectText);
    }
}
