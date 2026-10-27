namespace AXamlIDE.Models;

/// <summary>What the IDE shows when it starts (Visual Studio's Options > Environment > Startup).</summary>
public enum StartupAction
{
    /// <summary>The start window: recent solutions and folders, Clone, Open, Create a new project.</summary>
    StartWindow,

    /// <summary>Reopen the solution or folder that was open last.</summary>
    LastWorkspace,

    /// <summary>The IDE with nothing open.</summary>
    Empty
}
