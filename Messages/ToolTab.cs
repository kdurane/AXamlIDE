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
    public const string Terminal = "terminal";
    public const string Tests = "tests";
    public const string Search = "search";

    /// <summary>VS Code's Run and Debug view: a side bar tab while debugging (DebugSideBarViewModel).</summary>
    public const string Debug = "debug";

    // Sections of the Debug tab rather than panels of their own: showing one brings the Debug tab to the front and
    // opens that section (Debug > Windows > Locals, Watch, Call Stack, Breakpoints, Diagnostic Tools).
    public const string Locals = "locals";
    public const string Watch = "watch";
    public const string CallStack = "callstack";
    public const string Breakpoints = "breakpoints";
    public const string Diagnostics = "diagnostics";

    /// <summary>The ids that are sections of the Debug tab.</summary>
    public static bool IsDebugSection(string id) => id is Locals or Watch or CallStack or Breakpoints or Diagnostics;
}
