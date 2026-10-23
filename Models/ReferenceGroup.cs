using System.Collections.Generic;
using AXamlIDE.Services.Lsp;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.Models;

/// <summary>One reference in the references list: its line number and its line of code, the reference highlighted.</summary>
public sealed record ReferenceItem(LspLocation Location, ReferencePreview Preview)
{
    /// <summary>1-based, as the editor shows it.</summary>
    public int LineNumber => Location.Range.StartLine + 1;
}

/// <summary>
/// One file's references in the references list (a tree, as VS Code's peek and Rider's usages list): the file's name,
/// the folder it's in, how many there are. A source generator's output is a group too, collapsed to begin with: its
/// references are mostly generated doc comments (&lt;see cref="..."/&gt;), seldom what's being looked for.
/// </summary>
public sealed partial class ReferenceGroup(string name, string? folder, bool isSourceGenerated, IReadOnlyList<ReferenceItem> items)
    : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>The folder it's in ("ViewModels"), or "source generated".</summary>
    public string? Folder { get; } = folder;

    public bool IsSourceGenerated { get; } = isSourceGenerated;

    public IReadOnlyList<ReferenceItem> Items { get; } = items;

    public int Count => Items.Count;

    [ObservableProperty] public partial bool IsExpanded { get; set; } = !isSourceGenerated;
}
