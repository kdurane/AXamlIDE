using System.Collections.Generic;
using System.Linq;

namespace AXamlIDE.Services.Keymaps;

/// <summary>Where a shortcut works.</summary>
public enum CommandScope
{
    /// <summary>Anywhere in the IDE's windows.</summary>
    Global,

    /// <summary>Only while the text editor has the keyboard focus (so F2 in the explorer still renames the file).</summary>
    TextEditor
}

/// <summary>
/// A command a keymap can give a shortcut to. <see cref="Id"/> is what keymap files use, named like Visual
/// Studio's (Category.Command), so it never changes even when the menu text does.
/// </summary>
public sealed record IdeCommandInfo(string Id, string Name, string Description, CommandScope Scope = CommandScope.Global)
{
    /// <summary>The part before the dot: "Edit" for Edit.ToggleComment.</summary>
    public string Category => Id[..Id.IndexOf('.')];
}

/// <summary>
/// Every command that can have a shortcut, with the short description the Settings > Keyboard page shows.
/// To add one: add it here, wire its id to a command in Shell/KeyMap.cs, and give it keys in the keymap files
/// (Services/Keymaps/*.json). The app refuses to start if KeyMap and this list disagree.
/// </summary>
public static class CommandCatalog
{
    private const CommandScope Editor = CommandScope.TextEditor;

    public static IReadOnlyList<IdeCommandInfo> All { get; } =
    [
        // ---- File
        new("File.OpenFolder", "Open Folder", "Open a folder as the workspace."),
        new("File.OpenSolution", "Open Solution", "Open a .sln or .slnx solution."),
        new("File.Save", "Save", "Save the current document."),
        new("File.Close", "Close Tab", "Close the current document."),
        new("File.NewItem", "New Item", "Add a new file from a template to the folder selected in the explorer."),
        new("File.NewClass", "New Class", "Add a new C# class to the folder selected in the explorer."),
        new("File.NewFolder", "New Folder", "Add a new folder to the folder selected in the explorer."),

        // ---- Edit
        new("Edit.ToggleComment", "Toggle Comment", "Comment out the selected lines, or uncomment them if they're already comments. In AXAML, whole elements.", Editor),
        new("Edit.CommentSelection", "Comment Selection", "Comment out the selected lines. In AXAML, whole elements.", Editor),
        new("Edit.UncommentSelection", "Uncomment Selection", "Remove the comment markers from the selected lines.", Editor),
        new("Edit.FormatDocument", "Format Document", "Reformat the current document."),
        new("Edit.FindInFiles", "Find in Files", "Search every file in the workspace."),
        new("Edit.Rename", "Rename Symbol", "Rename the C# symbol at the caret and every reference to it.", Editor),
        new("Edit.QuickActions", "Quick Actions", "Show the fixes and refactorings for the caret (the light bulb).", Editor),
        new("Edit.CompleteWord", "Complete Word", "Show the completion list.", Editor),
        new("Edit.ParameterInfo", "Parameter Info", "Show the parameters of the method being called.", Editor),
        new("Edit.InsertSnippet", "Insert Snippet", "Pick a code snippet to insert at the caret.", Editor),

        // ---- View
        new("View.ToggleSideBar", "Show/Hide Side Bar", "Show or hide the side bar."),
        new("View.Explorer", "Explorer", "Show the Explorer."),
        new("View.Git", "Git", "Show the Git panel."),
        new("View.Properties", "Properties", "Show the properties of the item selected in the explorer."),
        new("View.Problems", "Problems", "Show the Problems list."),
        new("View.Output", "Output", "Show the Output panel."),
        new("View.DebugConsole", "Debug Console", "Show the Debug Console."),

        // ---- Navigate
        new("Navigate.GoToFile", "Go to File", "Find a file by name and open it."),
        new("Navigate.GoToSymbol", "Go to Symbol", "Find a type or member anywhere in the workspace."),
        new("Navigate.GoToDefinition", "Go to Definition", "Jump to where the symbol at the caret is declared.", Editor),
        new("Navigate.FindAllReferences", "Find All References", "List every use of the symbol at the caret.", Editor),
        new("Navigate.NextProblem", "Next Problem", "Go to the next error or warning."),
        new("Navigate.PreviousProblem", "Previous Problem", "Go to the previous error or warning."),

        // ---- Build (the startup project)
        new("Build.Build", "Build", "Build the startup project."),
        new("Build.Rebuild", "Rebuild", "Clean, then build the startup project."),
        new("Build.Clean", "Clean", "Delete the startup project's build output."),
        new("Build.Publish", "Publish", "Publish the selected project."),

        // ---- Debug
        new("Debug.StartOrContinue", "Start Debugging / Continue", "Start the startup project under the debugger, or continue when paused."),
        new("Debug.StartWithoutDebugging", "Start Without Debugging", "Run the startup project without the debugger."),
        new("Debug.Stop", "Stop", "Stop the running project."),
        new("Debug.Restart", "Restart", "Stop and start debugging again."),
        new("Debug.Pause", "Pause", "Pause the program being debugged."),
        new("Debug.StepOver", "Step Over", "Run the current line and stop at the next one."),
        new("Debug.StepInto", "Step Into", "Step into the call on the current line."),
        new("Debug.StepOut", "Step Out", "Run to the end of the current method."),
        new("Debug.AddWatch", "Add Watch", "Watch the selection, or the expression at the caret.", Editor),
        new("Debug.Watch", "Watch Window", "Show the Watch window."),
        new("Debug.Locals", "Locals Window", "Show the Locals window."),
        new("Debug.CallStack", "Call Stack Window", "Show the Call Stack window."),

        // ---- Tools
        new("Tools.Settings", "Settings", "Open the settings window."),
    ];

    private static readonly Dictionary<string, IdeCommandInfo> ById = All.ToDictionary(c => c.Id);

    public static IdeCommandInfo? Find(string id) => ById.GetValueOrDefault(id);
}
