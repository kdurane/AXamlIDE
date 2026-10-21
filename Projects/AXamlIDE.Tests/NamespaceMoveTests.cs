using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Tests;

/// <summary>Moving files in the Avalonia app template's layout (App, Views/MainWindow, ViewModels/...).</summary>
public sealed class NamespaceMoveTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public NamespaceMoveTests()
    {
        _workspace.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.Write("App/App.axaml.cs", """
            using Avalonia;
            using App.ViewModels;
            using App.Views;

            namespace App;

            public partial class App : Application
            {
                void Start() => new MainWindow { DataContext = new MainViewModel() };
            }
            """);
        _workspace.Write("App/Views/MainWindow.axaml", """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:vm="using:App.ViewModels"
                    x:Class="App.Views.MainWindow"
                    x:DataType="vm:MainViewModel" />
            """);
        _workspace.Write("App/Views/MainWindow.axaml.cs", """
            using Avalonia.Controls;

            namespace App.Views;

            public partial class MainWindow : Window
            {
            }
            """);
        _workspace.Write("App/ViewModels/ViewModelBase.cs", """
            namespace App.ViewModels;

            public abstract class ViewModelBase { }
            """);
        _workspace.Write("App/ViewModels/MainViewModel.cs", """
            namespace App.ViewModels;

            public partial class MainViewModel : ViewModelBase
            {
                public string Greeting => "Welcome to Avalonia!";
            }
            """);
    }

    public void Dispose() => _workspace.Dispose();

    private string PathOf(string relative) => Path.Combine(_workspace.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private Dictionary<string, NamespaceFileUpdate> Plan(string source, string target)
        => NamespaceMove.Plan(PathOf(source), PathOf(target), _workspace.Root, new Dictionary<string, string>())
            .ToDictionary(update => Path.GetRelativePath(_workspace.Root, update.Path).Replace(Path.DirectorySeparatorChar, '/'));

    [Fact]
    public void Moving_a_view_changes_its_namespace_and_x_class_and_points_usings_at_the_new_one()
    {
        var updates = Plan("App/Views/MainWindow.axaml", "App/ViewModels");

        Assert.Contains("namespace App.ViewModels;", updates["App/ViewModels/MainWindow.axaml.cs"].UpdatedText);
        Assert.Contains("x:Class=\"App.ViewModels.MainWindow\"", updates["App/ViewModels/MainWindow.axaml"].UpdatedText);

        // Views is empty now, so its using would be an error; App already has using App.ViewModels.
        var app = updates["App/App.axaml.cs"].UpdatedText;
        Assert.DoesNotContain("using App.Views;", app);
        Assert.Single(app.Split('\n'), line => line.Trim() == "using App.ViewModels;");
    }

    [Fact]
    public void Moving_a_view_model_keeps_the_xaml_working_through_a_new_prefix()
    {
        var updates = Plan("App/ViewModels/MainViewModel.cs", "App/Views");

        Assert.Contains("namespace App.Views;", updates["App/Views/MainViewModel.cs"].UpdatedText);
        // ViewModelBase stayed behind: the moved file needs a using for it.
        Assert.Contains("using App.ViewModels;", updates["App/Views/MainViewModel.cs"].UpdatedText);

        // MainWindow.axaml is in App.Views now too, but it reached MainViewModel through vm: (still in use for others).
        var xaml = updates["App/Views/MainWindow.axaml"].UpdatedText;
        Assert.Contains("xmlns:views=\"using:App.Views\"", xaml);
        Assert.Contains("x:DataType=\"views:MainViewModel\"", xaml);
        Assert.Contains("xmlns:vm=\"using:App.ViewModels\"", xaml);

        // App already has using App.Views, and ViewModels still has ViewModelBase: nothing to do there.
        Assert.False(updates.ContainsKey("App/App.axaml.cs"));
    }

    [Fact]
    public void Moving_the_whole_folder_renames_its_namespace_everywhere()
    {
        var updates = Plan("App/ViewModels", "App/Views");   // App/Views/ViewModels

        Assert.Contains("namespace App.Views.ViewModels;", updates["App/Views/ViewModels/MainViewModel.cs"].UpdatedText);
        Assert.Contains("namespace App.Views.ViewModels;", updates["App/Views/ViewModels/ViewModelBase.cs"].UpdatedText);
        Assert.Contains("using App.Views.ViewModels;", updates["App/App.axaml.cs"].UpdatedText);
        Assert.Contains("xmlns:vm=\"using:App.Views.ViewModels\"", updates["App/Views/MainWindow.axaml"].UpdatedText);
    }

    [Fact]
    public void Added_lines_are_laid_out_like_the_ones_around_them()
    {
        var updates = Plan("App/ViewModels/MainViewModel.cs", "App/Views");

        Assert.Equal("""
            using App.ViewModels;

            namespace App.Views;

            public partial class MainViewModel : ViewModelBase
            {
                public string Greeting => "Welcome to Avalonia!";
            }
            """, updates["App/Views/MainViewModel.cs"].UpdatedText);
        Assert.Equal("""
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:vm="using:App.ViewModels"
                    xmlns:views="using:App.Views"
                    x:Class="App.Views.MainWindow"
                    x:DataType="views:MainViewModel" />
            """, updates["App/Views/MainWindow.axaml"].UpdatedText);
    }

    [Fact]
    public void A_file_with_its_own_namespace_is_left_alone()
    {
        _workspace.Write("App/Views/Custom.cs", "namespace Somewhere.Else;\n\npublic class Custom { }\n");
        Assert.Empty(Plan("App/Views/Custom.cs", "App/ViewModels"));
    }

    [Fact]
    public void Fully_qualified_names_follow_the_move()
    {
        _workspace.Write("App/Program.cs", "namespace App;\n\nclass Program { App.Views.MainWindow? w; global::App.Views.MainWindow? g; }\n");
        var program = Plan("App/Views/MainWindow.axaml", "App/ViewModels")["App/Program.cs"].UpdatedText;
        Assert.Contains("App.ViewModels.MainWindow? w", program);
        Assert.Contains("global::App.ViewModels.MainWindow? g", program);
    }

    [Fact]
    public void Nothing_to_do_for_a_non_code_file_or_outside_a_project()
    {
        _workspace.Write("App/Assets/logo.txt", "logo");
        Assert.Empty(Plan("App/Assets/logo.txt", "App/Views"));
    }
}
