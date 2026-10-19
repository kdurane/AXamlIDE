using System.IO;
using AXamlIDE.Services.Running;
using Xunit;

namespace AXamlIDE.Tests;

public sealed class StartupProjectFinderTests
{
    [Theory]
    [InlineData("Exe", true)]
    [InlineData("winexe", true)]
    [InlineData("Library", false)]
    [InlineData(null, false)]
    public void IsRunnable_OnlyForExecutables(string? outputType, bool expected)
        => Assert.Equal(expected, StartupProjectFinder.IsRunnable(outputType));

    [Fact]
    public void FindProjectFiles_FindsNestedProjects_SkippingBuildOutputAndHiddenFolders()
    {
        using var workspace = new TempWorkspace();
        var app = workspace.Write("App/App.csproj", "<Project />");
        var lib = workspace.Write("src/Lib/Lib.csproj", "<Project />");
        workspace.Write("App/bin/Debug/Copy.csproj", "<Project />");
        workspace.Write("App/obj/Temp.csproj", "<Project />");
        workspace.Write(".git/Hidden.csproj", "<Project />");
        workspace.Write("node_modules/pkg/Pkg.csproj", "<Project />");

        var found = StartupProjectFinder.FindProjectFiles(workspace.Root);

        Assert.Equal([app, lib], found);
    }
}
