namespace AXamlIDE.Models;

/// <summary>One entry in the toolbar's startup project box.</summary>
/// <param name="Name">What the box shows: the project's name (with its folder when two projects share a name).</param>
/// <param name="Path">The project file.</param>
public sealed record StartupProjectItem(string Name, string Path);
