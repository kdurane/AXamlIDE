using System.Collections.Generic;

namespace AXamlIDE.Models;

/// <summary>
/// What AXamlIDE remembers about one workspace between runs (kept in its hidden <c>.axamlide/</c> folder):
/// the files that were open and the layout. Paths are relative to the workspace folder when inside it.
/// </summary>
public sealed record WorkspaceState
{
    public int SchemaVersion { get; init; } = 1;

    public List<OpenFileState> OpenFiles { get; init; } = [];

    /// <summary>The file that was selected, or null.</summary>
    public string? ActiveFile { get; init; }

    /// <summary>
    /// Layout numbers by name: dock proportions ("ExplorerTools", "BottomTools"...) and floating window
    /// bounds ("SearchWindow.X"...). Unknown names are ignored, so the layout can grow without a new schema.
    /// </summary>
    public Dictionary<string, double> Layout { get; init; } = [];

    /// <summary>The project Run and Debug start (the toolbar's startup project), or null if none was chosen.</summary>
    public string? StartupProject { get; init; }

    /// <summary>The build configuration chosen in the toolbar ("Debug", "Release"), or null for the default.</summary>
    public string? Configuration { get; init; }
}

/// <summary>One open file: where the caret and scroll were, and (for AXAML) how the preview was laid out.</summary>
public sealed record OpenFileState
{
    public required string Path { get; init; }
    public int CaretOffset { get; init; }
    public double VerticalOffset { get; init; }
    public string? PreviewMode { get; init; }
    public string? PreviewSplit { get; init; }
    public bool PreviewFirst { get; init; }
}
