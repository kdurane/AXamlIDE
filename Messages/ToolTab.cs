namespace AXamlIDE.Messages;

/// <summary>
/// Ids of the tool panels, used with <see cref="ShowToolTabMessage"/>. Where each starts in the
/// layout is in ViewModels/DockFactory.cs; its view is in Shell/ToolPanels.cs.
/// </summary>
public static class ToolTab
{
    public const string Explorer = "explorer";
    public const string Git = "git";
    public const string Properties = "properties";
    public const string Problems = "problems";
    public const string Output = "output";
    public const string Console = "console";
    public const string Search = "search";
}
