using System.IO;
using AXamlIDE.Models;
using AXamlIDE.Services.Workspace;
using Xunit;

namespace AXamlIDE.Tests;

public sealed class WorkspaceStateStoreTests
{
    private readonly WorkspaceStateStore _store = new();

    [Fact]
    public void Load_WithNoStateFolder_ReturnsEmptyState()
    {
        using var workspace = new TempWorkspace();

        var state = _store.Load(workspace.Root);

        Assert.Empty(state.OpenFiles);
        Assert.Null(state.ActiveFile);
        Assert.Empty(state.Layout);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFilesAndLayout_StoringPathsInsideTheWorkspaceRelatively()
    {
        using var workspace = new TempWorkspace();
        var inside = workspace.Write("src/MainView.axaml", "<UserControl />");
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere.cs");
        var state = new WorkspaceState
        {
            OpenFiles =
            [
                new OpenFileState { Path = inside, CaretOffset = 12, VerticalOffset = 40, PreviewMode = "Split", PreviewFirst = true },
                new OpenFileState { Path = outside, CaretOffset = 3 }
            ],
            ActiveFile = inside,
            Layout = { ["ExplorerTools"] = 0.25 }
        };

        Assert.Null(_store.Save(workspace.Root, state));
        var json = File.ReadAllText(WorkspaceStateStore.StatePath(workspace.Root));
        var loaded = _store.Load(workspace.Root);

        Assert.Contains("\"src", json);                     // stored relative to the workspace
        Assert.DoesNotContain(workspace.Root, json);
        Assert.Equal(inside, loaded.OpenFiles[0].Path);      // loaded absolute again
        Assert.Equal(12, loaded.OpenFiles[0].CaretOffset);
        Assert.True(loaded.OpenFiles[0].PreviewFirst);
        Assert.Equal(outside, loaded.OpenFiles[1].Path);     // outside the workspace: kept absolute
        Assert.Equal(inside, loaded.ActiveFile);
        Assert.Equal(0.25, loaded.Layout["ExplorerTools"]);
    }

    [Fact]
    public void Load_WithInvalidJson_ReturnsEmptyState()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(".axamlide/workspace.json", "{ not json");

        Assert.Empty(_store.Load(workspace.Root).OpenFiles);
    }
}
