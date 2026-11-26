namespace AXamlIDE.Models;

/// <summary>A row of the Back button's drop-down: where it goes ("Program.cs: Line 12") and how many steps back that is.</summary>
public sealed record NavigationEntry(string Title, int Steps);
