using System;
using System.IO;
using System.Linq;
using AXamlIDE.Services.Git;
using AXamlIDE.Services.Workspace;
using Xunit;

namespace AXamlIDE.Tests;

/// <summary>The start window's Core parts: the recent list, cloning's names, and the New Project wizard's templates.</summary>
public sealed class StartWindowTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 28, 15, 0, 0);   // a Wednesday
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // ------------------------------------------------------------- recent list

    [Fact]
    public void Opening_moves_an_entry_to_the_top_and_keeps_its_pin()
    {
        var a = Path.GetFullPath("/x/A.sln");
        var b = Path.GetFullPath("/x/B");
        RecentWorkspace[] items = [new(b, Now.AddHours(-1)), new(a, Now.AddDays(-3), Pinned: true)];

        var after = RecentWorkspaces.Opened(items, a, Now);

        Assert.Equal([a, b], after.Select(item => item.Path).ToArray());
        Assert.True(after[0].Pinned);
        Assert.Equal(Now, after[0].LastOpened);
    }

    [Fact]
    public void Only_the_newest_unpinned_entries_are_kept()
    {
        var pinned = new RecentWorkspace(Path.GetFullPath("/old/Pinned"), Now.AddYears(-1), Pinned: true);
        var many = Enumerable.Range(0, RecentWorkspaces.MaxUnpinned + 5)
            .Select(i => new RecentWorkspace(Path.GetFullPath($"/p/{i}"), Now.AddMinutes(-i - 1)))
            .Append(pinned).ToList();

        var after = RecentWorkspaces.Opened(many, "/new", Now);

        Assert.Equal(RecentWorkspaces.MaxUnpinned, after.Count(item => !item.Pinned));
        Assert.Contains(pinned, after);
        Assert.DoesNotContain(after, item => item.Path == Path.GetFullPath($"/p/{RecentWorkspaces.MaxUnpinned - 1}"));
    }

    [Fact]
    public void Groups_are_pinned_then_by_when_last_opened()
    {
        RecentWorkspace[] items =
        [
            new("/a", Now.AddHours(-2)),
            new("/b", Now.AddDays(-1)),
            new("/c", Now.AddDays(-2)),           // Monday: this week
            new("/d", Now.AddDays(-10)),          // the 18th: this month
            new("/e", Now.AddMonths(-3)),
            new("/f", Now.AddMonths(-6), Pinned: true)
        ];

        var groups = RecentWorkspaces.Group(items, Now);

        Assert.Equal(["Pinned", "Today", "Yesterday", "This week", "This month", "Older"], groups.Select(group => group.Title).ToArray());
        Assert.Equal("/f", Assert.Single(groups[0].Items).Path);
    }

    [Fact]
    public void The_list_saves_and_loads_back()
    {
        var store = new RecentWorkspaces(Path.Combine(_workspace.Root, "recent.json"));
        RecentWorkspace[] items = [new(Path.GetFullPath("/x/A.slnx"), Now, Pinned: true)];

        Assert.True(store.Save(items));

        Assert.Equal(items, store.Load());
        Assert.Empty(new RecentWorkspaces(Path.Combine(_workspace.Root, "missing.json")).Load());
    }

    [Fact]
    public void Names_come_from_the_solution_or_folder()
    {
        Assert.Equal("App", new RecentWorkspace("/src/App/App.slnx", Now).Name);
        Assert.True(new RecentWorkspace("/src/App/App.sln", Now).IsSolution);
        Assert.Equal("Tools", new RecentWorkspace("/src/Tools", Now).Name);
    }

    // ------------------------------------------------------------- clone

    [Theory]
    [InlineData("https://github.com/AvaloniaUI/Avalonia.git", "Avalonia")]
    [InlineData("https://github.com/AvaloniaUI/Avalonia/", "Avalonia")]
    [InlineData("git@github.com:owner/my-repo.git", "my-repo")]
    [InlineData("git@host:repo", "repo")]
    [InlineData("https://github.com/", "github.com")]
    [InlineData("", "")]
    [InlineData("https:", "")]
    public void The_clone_folder_is_named_after_the_repository(string url, string name)
        => Assert.Equal(name, GitClone.RepositoryName(url));

    [Fact]
    public void After_cloning_the_one_solution_is_opened_else_the_folder()
    {
        var one = Path.Combine(_workspace.Root, "one");
        _workspace.Write("one/App.slnx", "");
        _workspace.Write("two/A.sln", "");
        _workspace.Write("two/B.sln", "");

        Assert.Equal(Path.Combine(one, "App.slnx"), GitClone.PathToOpen(one));
        Assert.Equal(Path.Combine(_workspace.Root, "two"), GitClone.PathToOpen(Path.Combine(_workspace.Root, "two")));
    }

    // ------------------------------------------------------------- new project

    [Fact]
    public void Templates_are_read_from_the_table_by_its_dashes()
    {
        string[] output =
        [
            "These templates matched your input: --type='project'",
            "",
            "Template Name                                 Short Name           Language    Tags",
            "--------------------------------------------  -------------------  ----------  -----------------------------------------",
            "ASP.NET Core Web App (Razor Pages)            webapp,razor         [C#]        Web/MVC/Razor Pages",
            "Avalonia .NET MVVM App                        avalonia.mvvm        [C#],F#     Desktop/Xaml/Avalonia/Windows/Linux/macOS",
            "Console App                                   console              [C#],F#,VB  Common/Console"
        ];

        var templates = ProjectTemplates.ParseList(output);

        Assert.Equal(3, templates.Count);
        Assert.Equal(new[] { "webapp", "avalonia.mvvm", "console" }, templates.Select(template => template.ShortName));
        Assert.Equal("ASP.NET Core Web App (Razor Pages)", templates[0].Name);
        Assert.Equal(["Web", "MVC", "Razor Pages"], templates[0].Tags);
        Assert.Equal(["C#", "F#", "VB"], templates[2].Languages);
        Assert.True(templates[1].IsAvalonia);
        Assert.False(templates[2].IsAvalonia);
        Assert.Empty(ProjectTemplates.ParseList(["No templates found."]));
    }

    [Fact]
    public void The_solution_goes_beside_or_around_the_project()
    {
        var template = new ProjectTemplate("Console App", "console", ["C#"], []);
        var location = _workspace.Root;

        var apart = new NewProjectRequest(template, "App", location, "Sln", SolutionInProjectFolder: false);
        var together = apart with { SolutionInProjectFolder = true };

        Assert.Equal(Path.Combine(location, "Sln"), apart.SolutionFolder);
        Assert.Equal(Path.Combine(location, "Sln", "App"), apart.ProjectFolder);
        Assert.Equal(Path.Combine(location, "App"), together.SolutionFolder);
        Assert.Equal(Path.Combine(location, "App"), together.ProjectFolder);
    }

    [Fact]
    public void Bad_names_and_used_folders_are_refused()
    {
        var template = new ProjectTemplate("Console App", "console", ["C#"], []);
        var ok = new NewProjectRequest(template, "App", _workspace.Root, "App", SolutionInProjectFolder: false);
        _workspace.Write("Taken/file.txt", "");

        Assert.Null(ProjectTemplates.Problem(ok));
        Assert.NotNull(ProjectTemplates.Problem(ok with { ProjectName = "" }));
        Assert.NotNull(ProjectTemplates.Problem(ok with { ProjectName = "a/b" }));
        Assert.NotNull(ProjectTemplates.Problem(ok with { ProjectName = " App" }));
        Assert.NotNull(ProjectTemplates.Problem(ok with { SolutionName = "Taken" }));
        Assert.NotNull(ProjectTemplates.Problem(ok with { Location = "relative" }));
        Assert.Null(ProjectTemplates.Problem(ok with { SolutionName = "", SolutionInProjectFolder = true }));
    }
}
