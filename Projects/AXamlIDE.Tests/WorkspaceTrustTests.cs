using System;
using System.IO;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Tests;

public sealed class WorkspaceTrustTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "axamlide-tests-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_directory, "trusted.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static string P(string path) => Path.GetFullPath(path);

    [Fact]
    public void A_folder_under_a_trusted_folder_is_trusted()
    {
        string[] trusted = [P("/home/me/Projects")];

        Assert.True(WorkspaceTrust.IsTrusted(trusted, P("/home/me/Projects")));
        Assert.True(WorkspaceTrust.IsTrusted(trusted, P("/home/me/Projects/App/")));
        Assert.False(WorkspaceTrust.IsTrusted(trusted, P("/home/me/ProjectsOld/App")));   // a name that only starts the same
        Assert.False(WorkspaceTrust.IsTrusted(trusted, P("/home/me")));
    }

    [Fact]
    public void A_solution_is_judged_by_its_folder()
    {
        Assert.Equal(P("/home/me/App"), WorkspaceTrust.FolderOf("/home/me/App/App.sln"));
        Assert.Equal(P("/home/me/App"), WorkspaceTrust.FolderOf("/home/me/App/App.slnx"));
        Assert.Equal(P("/home/me/App"), WorkspaceTrust.FolderOf("/home/me/App/"));
    }

    [Fact]
    public void Trusting_a_parent_replaces_the_folders_under_it()
    {
        var list = WorkspaceTrust.Trusted([P("/p/a"), P("/p/b"), P("/q")], "/p");

        Assert.Equal([P("/q"), P("/p")], list);
        Assert.Same(list, WorkspaceTrust.Trusted(list, "/p/c"));   // already covered: unchanged
    }

    [Fact]
    public void Trust_is_saved_and_loads_back()
    {
        var root = Path.Combine(_directory, "repos");
        new WorkspaceTrust(StorePath).Trust(root);

        var loaded = new WorkspaceTrust(StorePath);
        Assert.True(loaded.IsTrusted(Path.Combine(root, "App", "App.sln")));
        Assert.False(loaded.IsTrusted(Path.Combine(_directory, "Downloads", "App.sln")));
    }

    [Fact]
    public void The_recent_list_is_trusted_only_the_first_time()
    {
        var first = Path.Combine(_directory, "first", "First.sln");
        var later = Path.Combine(_directory, "later");

        new WorkspaceTrust(StorePath).StartWith([first]);
        new WorkspaceTrust(StorePath).StartWith([later]);   // trusted.json exists now: ignored

        var trust = new WorkspaceTrust(StorePath);
        Assert.True(trust.IsTrusted(first));
        Assert.False(trust.IsTrusted(later));
    }

    [Fact]
    public void Nothing_is_trusted_without_a_file()
        => Assert.False(new WorkspaceTrust(StorePath).IsTrusted(Path.Combine(_directory, "App")));
}
