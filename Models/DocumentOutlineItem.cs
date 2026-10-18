namespace AXamlIDE.Models;

/// <summary>One entry in the C# outline (a class, method, property...), with offsets into its document.</summary>
public sealed record DocumentOutlineItem(
    string DisplayName,
    int Kind,
    int StartOffset,
    int EndOffset,
    int NavigationOffset);
