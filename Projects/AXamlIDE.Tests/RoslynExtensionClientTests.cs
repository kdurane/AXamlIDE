using System.Linq;
using System.Text.Json;
using AXamlIDE.RoslynExtension;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Tests;

/// <summary>
/// The messages to and from our extension in the Roslyn server, written as the server writes them (System.Text.Json's
/// defaults, which is what it uses for a handler's answer) and read back as the IDE reads them.
/// </summary>
public class RoslynExtensionClientTests
{
    private static ExtensionDiagnostic Diagnostic(string code, int severity, int line, int start, int end, string message, string? help = null)
        => new(new ExtensionRange(new ExtensionPosition(line, start), new ExtensionPosition(line, end)), severity, code, message, "csharp",
            help is null ? null : new ExtensionCodeDescription(help), null);

    [Fact]
    public void Project_diagnostics_are_read_like_the_servers_own()
    {
        var json = JsonSerializer.Serialize(new ProjectDiagnosticsResponse(true, "v7",
        [
            new FileDiagnostics("/src/A.cs",
            [
                Diagnostic("CS0029", 1, 3, 12, 15, "Cannot implicitly convert type 'string' to 'int'", "https://example.org/CS0029"),
                Diagnostic("CS0169", 2, 1, 4, 11, "The field 'A._unused' is never used")
            ]),
            new FileDiagnostics("/src/Lib.csproj", [Diagnostic("CS0006", 1, 0, 0, 0, "Metadata file 'x.dll' could not be found")])
        ], Checked: 1, Milliseconds: 42));

        var parsed = RoslynExtensionClient.ParseDiagnostics(json);

        Assert.NotNull(parsed);
        Assert.Equal("v7", parsed.Version);
        Assert.Equal(1, parsed.Checked);
        Assert.Equal(42, parsed.Milliseconds);
        Assert.Equal(["/src/A.cs", "/src/Lib.csproj"], parsed.Files.Keys.Order());

        var error = parsed.Files["/src/A.cs"][0];
        Assert.Equal((3, 12, 3, 15), (error.StartLine, error.StartChar, error.EndLine, error.EndChar));
        Assert.Equal(LspDiagnosticSeverity.Error, error.Severity);
        Assert.Equal("CS0029: Cannot implicitly convert type 'string' to 'int'", error.Message);
        Assert.Equal("CS0029", LspHelpers.DiagnosticCode(error.Raw));
        Assert.Equal("https://example.org/CS0029", error.Raw!.Value.GetProperty("codeDescription").GetProperty("href").GetString());

        var warning = parsed.Files["/src/A.cs"][1];
        Assert.Equal(LspDiagnosticSeverity.Warning, warning.Severity);
        Assert.False(warning.Raw!.Value.TryGetProperty("codeDescription", out _));   // no help link: left out, not null
    }

    [Fact]
    public void A_project_that_is_gone_reads_as_null()
        => Assert.Null(RoslynExtensionClient.ParseDiagnostics(JsonSerializer.Serialize(new ProjectDiagnosticsResponse(false, "", []))));

    [Fact]
    public void Messages_use_the_names_the_other_side_expects()
    {
        Assert.Equal("""{"priorityPaths":["/src/A.cs"]}""", JsonSerializer.Serialize(new ProjectsRequest(["/src/A.cs"])));
        Assert.Equal("""{"projectId":"p1"}""", JsonSerializer.Serialize(new ProjectDiagnosticsRequest("p1")));

        var projects = JsonSerializer.Deserialize<ProjectsResponse>(
            """{"projects":[{"id":"8a03","name":"Core (net10.0)","filePath":"/src/Core.csproj","version":"v1"}]}""");
        Assert.Equal(new ProjectEntry("8a03", "Core (net10.0)", "/src/Core.csproj", "v1"), Assert.Single(projects!.Projects));
    }
}
