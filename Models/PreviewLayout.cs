namespace AXamlIDE.Models;

/// <summary>What an AXAML tab shows: just the code, code and preview, or just the preview.</summary>
public enum PreviewMode
{
    Code,
    Split,
    Design
}

/// <summary>How code and preview are split: side by side, or one above the other.</summary>
public enum PreviewSplit
{
    SideBySide,
    Stacked
}
