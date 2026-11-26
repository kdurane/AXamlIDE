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
        new("File.NewProject", "New Project", "Create a new project and solution from a template."),
        new("File.CloneRepository", "Clone Repository", "Clone a Git repository and open it."),
        new("File.StartWindow", "Start Window", "Show the start window: recent solutions and folders, clone, open or create."),
        new("File.Save", "Save", "Save the current document."),
        new("File.SaveAs", "Save As", "Save the current document under another name."),
        new("File.SaveAll", "Save All", "Save every document with unsaved changes."),
        new("File.Close", "Close Tab", "Close the current document."),
        new("File.CloseAll", "Close All Documents", "Close every document tab, asking about unsaved changes."),
        new("File.CloseAllButPinned", "Close All But Pinned", "Close every document tab that isn't pinned."),
        new("File.PinTab", "Pin Tab", "Pin the current tab (or unpin it): Close All But This / But Pinned keep it open."),
        new("File.NewItem", "New Item", "Add a new file from a template to the folder selected in the explorer."),
        new("File.NewClass", "New Class", "Add a new C# class to the folder selected in the explorer."),
        new("File.NewFolder", "New Folder", "Add a new folder to the folder selected in the explorer."),

        // ---- Edit
        new("Edit.ToggleComment", "Toggle Comment", "Comment out the selected lines, or uncomment them if they're already comments. In AXAML, whole elements.", Editor),
        new("Edit.CommentSelection", "Comment Selection", "Comment out the selected lines. In AXAML, whole elements.", Editor),
        new("Edit.UncommentSelection", "Uncomment Selection", "Remove the comment markers from the selected lines.", Editor),
        new("Edit.FormatDocument", "Format Document", "Reformat the current document."),
        new("Edit.ToggleBookmark", "Toggle Bookmark", "Bookmark the caret's line, or take its bookmark off."),
        new("Edit.NextBookmark", "Next Bookmark", "Go to the next bookmark, through the open documents."),
        new("Edit.PreviousBookmark", "Previous Bookmark", "Go to the previous bookmark, through the open documents."),
        new("Edit.ClearBookmarks", "Clear Bookmarks", "Remove every bookmark in the open documents."),
        new("Edit.Find", "Find", "Find text in the current document."),
        new("Edit.Replace", "Replace", "Find and replace text in the current document."),
        new("Edit.GoToLine", "Go To Line", "Go to a line number in the current document."),
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
        new("View.RunAndDebug", "Run and Debug", "Show the side bar's Debug tab: diagnostics, variables, watches and the call stack."),
        new("View.GitGraph", "Git Graph", "Show the Git panel's graph of commits and branches."),
        new("View.Properties", "Properties", "Show the properties of the item selected in the explorer."),
        new("View.Problems", "Problems", "Show the Problems list."),
        new("View.Output", "Output", "Show the Output panel."),
        new("View.DebugConsole", "Debug Console", "Show the Debug Console."),
        new("View.Terminal", "Terminal", "Show the Terminal and type a command."),
        new("View.TestExplorer", "Test Explorer", "Show the Tests panel."),
        new("View.TogglePanel", "Show/Hide Panel", "Show or hide the bottom panel (Problems, Output, Terminal...)."),
        new("View.ResetWindowLayout", "Reset Window Layout", "Put every panel back where it started, at its first size, and floating tabs back in the main window."),
        new("View.Claude", "Claude", "Open the Claude tab and type a message."),

        // ---- Claude
        new("Claude.NewChat", "New Claude Chat", "Start a new conversation in the Claude tab."),

        // ---- Git
        new("Git.GenerateCommitMessage", "Generate Commit Message", "Have Claude write the commit message for the changes about to be committed."),
        new("Git.Pull", "Git: Pull", "Pull the current branch from its remote."),
        new("Git.Push", "Git: Push", "Push the current branch (publishing it if it isn't on a remote yet)."),
        new("Git.Fetch", "Git: Fetch", "Fetch every remote's branches."),
        new("Git.Sync", "Git: Sync", "Pull, then push the current branch."),
        new("Git.UndoLastCommit", "Git: Undo Last Commit", "Take back the last commit, keeping its changes staged."),
        new("Git.DiscardAll", "Git: Discard All Changes", "Put every changed file back as in the last commit (asks first)."),
        new("Git.NewBranch", "Git: New Branch", "Create a branch and switch to it."),
        new("Git.SwitchBranch", "Git: Switch Branch", "Check out another branch."),
        new("Git.MergeBranch", "Git: Merge Branch", "Merge another branch into the current one."),
        new("Git.Stash", "Git: Stash Changes", "Put every change aside, new files too."),
        new("Git.NextChange", "Git: Next Change", "Go to the next changed block in the file and show what it was.", Editor),
        new("Git.PreviousChange", "Git: Previous Change", "Go to the previous changed block in the file and show what it was.", Editor),
        new("Git.RevertChange", "Git: Revert Change", "Put the changed block at the caret back as it is in git (Ctrl+Z undoes it).", Editor),
        new("Git.AcceptCurrentChange", "Git: Accept Current Change", "In the merge conflict at the caret, keep the current side (ours).", Editor),
        new("Git.AcceptIncomingChange", "Git: Accept Incoming Change", "In the merge conflict at the caret, keep the incoming side (theirs).", Editor),
        new("Git.AcceptBothChanges", "Git: Accept Both Changes", "In the merge conflict at the caret, keep both sides, current first.", Editor),

        // ---- Navigate
        new("Navigate.SearchEverywhere", "Search Everywhere", "Find types, files, symbols, actions and options by name, as Rider's (also double Shift)."),
        new("Navigate.FindAction", "Find Action", "Find an IDE command or option by name and run it (VS Code's Command Palette)."),
        new("Navigate.GoToFile", "Go to File", "Find a file by name and open it."),
        new("Navigate.GoToSymbol", "Go to Symbol", "Find a type or member anywhere in the workspace."),
        new("Navigate.GoToDefinition", "Go to Definition", "Jump to where the symbol at the caret is declared.", Editor),
        new("Navigate.FindAllReferences", "Find All References", "List every use of the symbol at the caret.", Editor),
        new("Navigate.Back", "Navigate Backward", "Go back to where you were before the last jump or tab switch."),
        new("Navigate.Forward", "Navigate Forward", "Go forward again after Navigate Backward."),
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
        new("Debug.ToggleBreakpoint", "Toggle Breakpoint", "Set or remove a breakpoint on the caret's line.", Editor),
        new("Debug.DeleteAllBreakpoints", "Delete All Breakpoints", "Remove every breakpoint, in open and closed files."),
        new("Debug.AddWatch", "Add Watch", "Watch the selection, or the expression at the caret.", Editor),
        new("Debug.Watch", "Watch Window", "Show the Debug tab's Watch section."),
        new("Debug.Locals", "Locals Window", "Show the Debug tab's Variables section."),
        new("Debug.CallStack", "Call Stack Window", "Show the Debug tab's Call Stack section."),
        new("Debug.Breakpoints", "Breakpoints Window", "Show the Debug tab's Breakpoints section."),
        new("Debug.DiagnosticTools", "Diagnostic Tools", "Show the Debug tab's memory and CPU graphs."),

        // ---- Test
        new("Test.RunAll", "Run All Tests", "Run every test in the workspace's test projects."),
        new("Test.RunFailed", "Run Failed Tests", "Run the tests that failed last time."),

        // ---- Analyze (Visual Studio's menu)
        new("Analyze.RunCodeAnalysisOnSolution", "Run Code Analysis on Solution", "Run every analyzer (IDE0005, CA1822...) over every file in the solution, once, and list what they find in Problems."),

        // ---- Tools
        new("Tools.SolutionWideAnalysis", "Solution-Wide Analysis", "Turn checking every file in the solution (not just the open ones) for compiler errors as you type on or off."),
        new("Tools.Settings", "Settings", "Open the settings window."),
    ];

    private static readonly Dictionary<string, IdeCommandInfo> ById = All.ToDictionary(c => c.Id);

    public static IdeCommandInfo? Find(string id) => ById.GetValueOrDefault(id);
}
