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

    /// <summary>The explorer nodes (folders, projects, files with children) that were expanded, by path.</summary>
    public List<string> ExpandedNodes { get; init; } = [];

    /// <summary>
    /// The collapsed fold lines of files that were closed (open files keep theirs in <see cref="OpenFiles"/>), by path,
    /// so a file reopens folded as it was left, as in Rider. Only folds: closed files don't keep a caret or scroll.
    /// </summary>
    public Dictionary<string, List<int>> ClosedFileFolds { get; init; } = [];

    /// <summary>Every breakpoint, in open and closed files (empty when Settings > Debugging says not to remember them).</summary>
    public List<BreakpointState> Breakpoints { get; init; } = [];

}

/// <summary>One remembered breakpoint: its file and line, whether it's on, and its Conditions.</summary>
public sealed record BreakpointState
{
    public required string Path { get; init; }
    public int Line { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
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

    /// <summary>Its tab was pinned.</summary>
    public bool IsPinned { get; init; }

    /// <summary>The bookmarked lines (1-based), or null for none.</summary>
    public List<int>? Bookmarks { get; init; }

    /// <summary>The lines whose fold was collapsed (#regions, usings, braces), or null to use the defaults.</summary>
    public List<int>? CollapsedFolds { get; init; }
}
