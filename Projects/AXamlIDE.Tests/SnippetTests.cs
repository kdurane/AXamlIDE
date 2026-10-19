using System;
using System.IO;
using System.Linq;
using AXamlIDE.Services.Snippets;

namespace AXamlIDE.Tests;

public sealed class SnippetTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("axamlide-snippets").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Parses_fields_mirrors_choices_variables_and_the_end()
    {
        var parts = SnippetBody.Parse("for (var ${1:i} = 0; $1 < ${2|a,b|}; $1++) { $TM_SELECTED_TEXT$0 } \\$x");

        Assert.Equal(
        [
            new SnippetText("for (var "), new SnippetField(1, "i"), new SnippetText(" = 0; "), new SnippetField(1, ""),
            new SnippetText(" < "), new SnippetField(2, "a"), new SnippetText("; "), new SnippetField(1, ""),
            new SnippetText("++) { "), new SnippetVariable("TM_SELECTED_TEXT", ""), new SnippetEnd(), new SnippetText(" } $x")
        ], parts);
    }

    [Theory]
    [InlineData("${1:${TM_FILENAME_BASE}}", "")]                 // a variable as a default: empty in a preview
    [InlineData("${1:outer ${2:inner}}", "outer inner")]          // nested placeholders flatten into the default
    [InlineData("price: $ 5", "price: $ 5")]                      // a lone $ stays text
    [InlineData("a \\} b", "a } b")]
    public void Previews_with_defaults(string body, string preview) => Assert.Equal(preview, SnippetBody.Preview(body));

    [Fact]
    public void Reads_VS_Code_snippet_files()
    {
        var snippets = SnippetLibrary.Parse("""
            {
                // comments are allowed, as in VS Code
                "Property": { "prefix": ["prop", "p"], "body": ["line 1", "line 2"], "description": "A property", },
                "Broken": "not an object"
            }
            """);

        Assert.Equal(["prop", "p"], snippets.Select(snippet => snippet.Prefix));
        Assert.All(snippets, snippet => Assert.Equal("line 1\nline 2", snippet.Body));
        Assert.Equal("A property", snippets[0].Description);
    }

    [Fact]
    public void User_snippets_replace_built_in_ones_with_the_same_prefix()
    {
        var library = new SnippetLibrary(_folder);
        Assert.Contains(library.For("csharp"), snippet => snippet.Prefix == "prop" && snippet.Name == "Property");

        File.WriteAllText(library.UserFilePath("csharp"), """{ "Mine": { "prefix": "prop", "body": "mine" } }""");

        var props = library.For("csharp").Where(snippet => snippet.Prefix == "prop").ToList();
        Assert.Equal("mine", Assert.Single(props).Body);
    }

    [Fact]
    public void The_new_user_file_parses_and_adds_nothing()
    {
        var library = new SnippetLibrary(_folder);
        var path = library.EnsureUserFile("axaml");

        Assert.Empty(SnippetLibrary.Parse(File.ReadAllText(path)));
        Assert.NotEmpty(library.For("axaml"));
    }

    [Fact]
    public void Built_in_snippets_parse()
    {
        foreach (var language in SnippetLibrary.Languages)
        {
            var snippets = new SnippetLibrary(_folder).For(language);
            Assert.True(snippets.Count > 10, language);
            Assert.All(snippets, snippet => Assert.NotEmpty(SnippetBody.Parse(snippet.Body)));
        }
    }
}

public sealed class IndentUnitTests
{
    [Theory]
    [InlineData("class A\n{\n    int x;\n        int y;\n}", "    ")]
    [InlineData("<a>\n  <b/>\n</a>", "  ")]
    [InlineData("class A\n{\n\tint x;\n}", "\t")]
    [InlineData("no indent at all", "    ")]
    [InlineData("a\n    \nb", "    ")]    // a line of only spaces says nothing
    public void Detects_the_files_indent(string text, string unit) => Assert.Equal(unit, IndentUnit.Detect(text));
}
