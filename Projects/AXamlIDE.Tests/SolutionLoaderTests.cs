using System;
using System.IO;
using System.Linq;
using AXamlIDE.Models;
using AXamlIDE.Services.Workspace;
using Xunit;

namespace AXamlIDE.Tests;

public sealed class SolutionLoaderTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly MsBuildHostClient _host = new(ProjectItemPropertiesTests.HostPath());

    public void Dispose()
    {
        _host.Dispose();
        _workspace.Dispose();
    }

    [Fact]
    public void LoadSolution_FindsProjectsWrittenWithWindowsSeparators()
    {
        const string sdkProject = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
        var app = _workspace.Write("App/App.csproj", sdkProject);
        var lib = _workspace.Write("src/Lib/Lib.csproj", sdkProject);
        var solution = _workspace.Write("Multi.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{603B47C3-D7E3-455A-836B-FF07F68449C3}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "src\Lib\Lib.csproj", "{07EE40E9-5D90-4D41-B169-4F99876FE6C7}"
            EndProject
            """);

        var root = SolutionLoader.LoadSolution(solution, _host);
        root.Populate();

        var projects = root.Children.Where(node => node.Kind == NodeKind.Project).Select(node => node.FullPath).ToList();
        Assert.Equal([app, lib], projects.Order().ToList());
    }
}
