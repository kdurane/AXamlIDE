using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Services.Search;

namespace AXamlIDE.Tests;

public sealed class WorkspaceSearchServiceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly WorkspaceSearchService _search = new();
    private static readonly Dictionary<string, string> NoOpenDocuments = [];

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Finds_matches_with_line_numbers()
    {
        var path = _workspace.Write("src/Hello.cs", "class A\n{\n    // Hello world\n}\n");

        var summary = await _search.SearchAsync(_workspace.Root, "Hello world", NoOpenDocuments);

        var result = Assert.Single(summary.Results);
        Assert.Equal(path, result.FilePath);
        Assert.Equal(3, result.LineNumber);
    }

    [Fact]
    public async Task Skips_build_output_folders()
    {
        _workspace.Write("bin/Debug/Copy.cs", "needle");
        _workspace.Write("obj/Generated.cs", "needle");
        var source = _workspace.Write("Real.cs", "needle");

        var summary = await _search.SearchAsync(_workspace.Root, "needle", NoOpenDocuments);

        Assert.Equal(source, Assert.Single(summary.Results).FilePath);
    }

    [Fact]
    public async Task Open_documents_are_searched_from_their_unsaved_text()
    {
        var path = _workspace.Write("Edited.cs", "old text on disk");
        var open = new Dictionary<string, string> { [Path.GetFullPath(path)] = "new unsaved text" };

        var summary = await _search.SearchAsync(_workspace.Root, "unsaved", open);

        Assert.Equal(path, Assert.Single(summary.Results).FilePath);
    }

    [Fact]
    public async Task Replace_writes_closed_files_and_edits_open_buffers()
    {
        var closed = _workspace.Write("Closed.cs", "var x = Foo; var y = Foo;");
        var openPath = Path.GetFullPath(_workspace.Write("Open.cs", "Foo"));
        var buffer = new Buffer("Foo");
        var openTexts = new Dictionary<string, string> { [openPath] = buffer.Text };

        var plan = await _search.PrepareReplaceAsync(_workspace.Root, "Foo", "Bar", openTexts);
        var summary = _search.ApplyReplacePlan(plan, new Dictionary<string, ITextBuffer> { [openPath] = buffer });

        Assert.Equal(3, plan.MatchCount);
        Assert.Equal("var x = Bar; var y = Bar;", File.ReadAllText(closed));
        Assert.Equal("Bar", buffer.Text);
        Assert.Equal("Foo", File.ReadAllText(openPath));   // open documents are left unsaved
        Assert.Equal(1, summary.ModifiedOpenDocuments);
    }

    [Fact]
    public async Task ListWorkspaceFiles_skips_build_output()
    {
        _workspace.Write("bin/x.dll", "");
        var source = _workspace.Write("a/b.cs", "");

        var files = await _search.ListWorkspaceFilesAsync(_workspace.Root);

        Assert.Equal([source], files.ToArray());
    }

    private sealed class Buffer(string text) : ITextBuffer
    {
        public string Text { get; set; } = text;
    }
}
