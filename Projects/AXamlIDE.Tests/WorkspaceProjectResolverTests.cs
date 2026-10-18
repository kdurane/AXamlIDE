using System;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Tests;

public sealed class WorkspaceProjectResolverTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly WorkspaceProjectResolver _resolver = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void The_innermost_solution_project_owns_the_file()
    {
        var outer = _workspace.Write("App/App.csproj", "<Project />");
        var inner = _workspace.Write("App/Plugins/Plugin.csproj", "<Project />");
        var file = _workspace.Write("App/Plugins/Thing.cs", "");

        Assert.Equal(inner, _resolver.FindProjectForDocument(file, [outer, inner], _workspace.Root));
    }

    [Fact]
    public void Without_a_solution_the_nearest_csproj_above_the_file_is_used()
    {
        var project = _workspace.Write("Lib/Lib.csproj", "<Project />");
        var file = _workspace.Write("Lib/Sub/Deep/Code.cs", "");

        Assert.Equal(project, _resolver.FindProjectForDocument(file, [], _workspace.Root));
    }

    [Fact]
    public void Two_projects_in_one_folder_is_ambiguous()
    {
        _workspace.Write("Mixed/A.csproj", "<Project />");
        _workspace.Write("Mixed/B.csproj", "<Project />");
        var file = _workspace.Write("Mixed/Code.cs", "");

        Assert.Null(_resolver.FindProjectForDocument(file, [], _workspace.Root));
    }
}
