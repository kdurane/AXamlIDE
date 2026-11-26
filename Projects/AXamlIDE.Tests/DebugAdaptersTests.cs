using System.Text.Json;
using AXamlIDE.Services.Debugging;

namespace AXamlIDE.Tests;

public class DebugAdaptersTests
{
    [Theory]
    [InlineData("10.0.13", 10, false)]
    [InlineData("11.0.0", 11, false)]
    [InlineData("11.0.0-rc.3.26522.148", 11, true)]
    public void Runtime_versions_are_read_from_their_folder_names(string text, int major, bool preview)
    {
        var version = DotNetRuntimes.RuntimeVersion.Parse(text)!;
        Assert.Equal(major, version.Major);
        Assert.Equal(preview, version.IsPreview);
    }

    [Fact]
    public void A_folder_name_that_isnt_a_version_is_skipped()
        => Assert.Null(DotNetRuntimes.RuntimeVersion.Parse("readme"));

    [Fact]
    public void RunInTerminal_arguments_give_the_command_line_folder_and_environment()
    {
        using var json = JsonDocument.Parse("""
            { "kind": "integrated", "title": "App", "cwd": "/src/App",
              "args": ["dotnet", "/src/App/bin/App.dll"],
              "env": { "DOTNET_DefaultDiagnosticPortSuspend": "1", "REMOVE_ME": null } }
            """);

        var launch = TerminalLaunch.From(json.RootElement)!;

        Assert.Equal("/src/App", launch.Folder);
        Assert.Equal(["dotnet", "/src/App/bin/App.dll"], launch.CommandLine);
        Assert.Equal("1", launch.Environment["DOTNET_DefaultDiagnosticPortSuspend"]);
        Assert.Null(launch.Environment["REMOVE_ME"]);
        Assert.Equal("App", launch.Title);
    }

    [Fact]
    public void RunInTerminal_without_a_command_line_is_refused()
    {
        using var json = JsonDocument.Parse("""{ "cwd": "/src", "args": [] }""");
        Assert.Null(TerminalLaunch.From(json.RootElement));
    }

    [Fact]
    public void Automatic_uses_netcoredbg_for_now()
        => Assert.StartsWith("netcoredbg is used for now", DebugAdapters.Describe(DebuggerChoice.Automatic));

    [Fact]
    public void Netcoredbg_is_used_when_chosen()
        => Assert.Equal("netcoredbg", DebugAdapters.Describe(DebuggerChoice.NetCoreDbg)[..10]);
}
