using System;
using System.IO;
using System.Linq;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Tests;

public sealed class ItemTemplatesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("axamlide-items").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ItemTemplate Template(string name) => ItemTemplates.All.Single(template => template.Name == name);

    [Fact]
    public void Namespace_is_the_root_namespace_plus_the_folders()
    {
        File.WriteAllText(Path.Combine(_root, "My.App.csproj"), "<Project><PropertyGroup><RootNamespace>Company.App</RootNamespace></PropertyGroup></Project>");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Views", "2D Shapes")).FullName;

        Assert.Equal("Company.App.Views._2D_Shapes", ItemTemplates.NamespaceFor(folder));
        Assert.Equal("Company.App", ItemTemplates.NamespaceFor(_root));
    }

    [Fact]
    public void Namespace_without_a_RootNamespace_is_the_project_name()
    {
        File.WriteAllText(Path.Combine(_root, "My.App.csproj"), "<Project />");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "ViewModels")).FullName;

        Assert.Equal("My.App.ViewModels", ItemTemplates.NamespaceFor(folder));
    }

    [Fact]
    public void User_control_writes_the_axaml_and_its_code_behind()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");

        var (files, error) = ItemTemplates.Plan(Template("User Control"), _root, "MainView.axaml");

        Assert.Null(error);
        Assert.Equal(["MainView.axaml", "MainView.axaml.cs"], files.Select(file => Path.GetFileName(file.Path)));
        Assert.Contains("x:Class=\"App.MainView\"", files[0].Text);
        Assert.Contains("public partial class MainView : UserControl", files[1].Text);
        Assert.Contains("namespace App;", files[1].Text);
    }

    [Theory]
    [InlineData("Class", "2Fast", "isn't a valid C# name")]
    [InlineData("Class", "", "Enter a name")]
    [InlineData("Class", "Existing", "already exists")]
    public void Plan_refuses_bad_names(string template, string name, string expected)
    {
        File.WriteAllText(Path.Combine(_root, "Existing.cs"), "");

        var (files, error) = ItemTemplates.Plan(Template(template), _root, name);

        Assert.Empty(files);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Write_creates_the_files_and_never_overwrites()
    {
        var (files, _) = ItemTemplates.Plan(Template("Text File"), _root, "notes");
        ItemTemplates.Write(files);

        Assert.True(File.Exists(Path.Combine(_root, "notes.txt")));
        Assert.Throws<IOException>(() => ItemTemplates.Write(files));
    }

    [Fact]
    public void Every_template_plans_without_error()
    {
        foreach (var template in ItemTemplates.All)
        {
            var (files, error) = ItemTemplates.Plan(template, _root, template.DefaultName);
            Assert.True(error is null, $"{template.Name}: {error}");
            Assert.NotEmpty(files);
            Assert.EndsWith(template.Extension, files[0].Path);
        }
    }
}
