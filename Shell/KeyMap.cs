using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AXamlIDE.Messages;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Shell;

/// <summary>One keyboard shortcut: the gesture, the command it runs, and the command parameter.</summary>
public sealed record KeyMapEntry(KeyGesture Gesture, ICommand Command, object? Parameter = null);

/// <summary>
/// Every keyboard shortcut in the app, in one table. <see cref="AttachTo"/> hooks it to a window
/// (MainWindow, the debug toolbar, and any future floating panel windows), so the same keys work
/// wherever the focus is.
///
/// Keys are caught on the tunnelling pass, before the text editor sees them, and a key is only
/// marked handled when its command can execute (e.g. F10 does nothing unless paused in the debugger).
/// Menu items show their keys from this table (<see cref="ShowGesturesIn"/>).
/// </summary>
public sealed class KeyMap
{
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Alt = KeyModifiers.Alt;
    private const KeyModifiers None = KeyModifiers.None;

    public KeyMap(MainWindowViewModel shell, PropertiesViewModel properties)
    {
        var editor = shell.EditorCommands;
        var run = shell.Run;

        Entries =
        [
            // ---- File
            Bind(Key.O, Ctrl | Shift, shell.Workspace.OpenSolutionCommand),
            Bind(Key.S, Ctrl, shell.DocumentCommands.SaveDocumentCommand),     // null parameter = the selected tab
            Bind(Key.W, Ctrl, shell.DocumentCommands.CloseDocumentCommand),
            Bind(Key.OemComma, Ctrl, shell.OpenSettingsCommand),
            Bind(Key.A, Ctrl | Shift, shell.Explorer.NewItemCommand),       // VS's Add New Item / Add Class; into the explorer selection
            Bind(Key.C, Shift | Alt, shell.Explorer.NewClassCommand),
            Bind(Key.N, Shift | Alt, shell.Explorer.NewFolderCommand),

            // ---- Edit (undo, cut, copy... are the editor's own keys)
            Bind(Key.R, Ctrl, editor.RenameSymbol),
            Bind(Key.OemPeriod, Ctrl, editor.QuickActions),
            Bind(Key.Space, Ctrl, editor.Completion),
            Bind(Key.Space, Ctrl | Shift, editor.SignatureHelp),
            Bind(Key.I, Ctrl | Shift, shell.DocumentCommands.FormatDocumentCommand),
            Bind(Key.F, Ctrl | Shift, shell.ShowFindInFilesCommand),
            Bind(Key.J, Ctrl, editor.InsertSnippet),                         // Rider's Insert Live Template (VS's is the chord Ctrl+K, X)

            // ---- View (VS Code's keys for the panels)
            Bind(Key.B, Ctrl, shell.ToggleExplorerCommand),
            Bind(Key.E, Ctrl | Shift, shell.ShowToolCommand, ToolTab.Explorer),
            Bind(Key.G, Ctrl | Shift, shell.ShowToolCommand, ToolTab.Git),
            Bind(Key.F4, None, properties.ShowCommand),       // Properties of the explorer selection
            Bind(Key.Enter, Alt, properties.ShowCommand),
            Bind(Key.M, Ctrl | Shift, shell.ShowToolCommand, ToolTab.Problems),
            Bind(Key.O, Ctrl | Alt, shell.ShowToolCommand, ToolTab.Output),     // VS (Ctrl+Shift+U is IBus's Unicode entry)
            Bind(Key.Y, Ctrl | Shift, shell.ShowToolCommand, ToolTab.Console),

            // ---- Navigate
            Bind(Key.P, Ctrl, editor.QuickOpen),
            Bind(Key.T, Ctrl, editor.WorkspaceSymbols),
            Bind(Key.F12, None, editor.GoToDefinition),
            Bind(Key.F12, Shift, editor.FindReferences),
            Bind(Key.F8, None, shell.Problems.NavigateDiagnosticCommand, true),
            Bind(Key.F8, Shift, shell.Problems.NavigateDiagnosticCommand, false),

            // ---- Build and Debug (Visual Studio layout); they act on the startup project
            Bind(Key.B, Ctrl | Shift, run.BuildCommand),
            Bind(Key.P, Shift | Alt, shell.Publish.PublishCurrentProjectCommand),   // VS has no default key for Publish
            Bind(Key.F5, Ctrl, run.RunProjectCommand),
            Bind(Key.F5, None, run.StartOrContinueDebuggingCommand),
            Bind(Key.F5, Shift, run.StopRunningProjectCommand),
            Bind(Key.F5, Ctrl | Shift, run.RestartDebuggingCommand),
            Bind(Key.Pause, Ctrl | Alt, run.PauseDebuggingCommand),   // Ctrl+Alt+Break
            Bind(Key.F10, None, run.StepOverCommand),
            Bind(Key.F11, None, run.StepIntoCommand),
            Bind(Key.F11, Shift, run.StepOutCommand),
            Bind(Key.F9, Shift, editor.AddWatch),
            Bind(Key.D3, Alt, shell.ShowToolCommand, ToolTab.Watch),       // VS's classic debugger window keys
            Bind(Key.D4, Alt, shell.ShowToolCommand, ToolTab.Locals),
            Bind(Key.D7, Alt, shell.ShowToolCommand, ToolTab.CallStack),
        ];
    }

    public IReadOnlyList<KeyMapEntry> Entries { get; }

    /// <summary>
    /// The key for a command (and parameter), for showing beside it in a menu or tooltip. A command with two keys
    /// (Properties: F4 and Alt+Enter) gives the first.
    /// </summary>
    public KeyGesture? GestureFor(ICommand? command, object? parameter = null)
    {
        foreach (var entry in Entries)
            if (ReferenceEquals(entry.Command, command) && Equals(entry.Parameter, parameter))
                return entry.Gesture;
        return null;
    }

    /// <summary>
    /// Show each menu item's key from this table (InputGesture), so the menus never disagree with it. Items whose
    /// key belongs to the control itself (the editor's Ctrl+Z, Ctrl+C...) keep the InputGesture written in XAML.
    /// </summary>
    public void ShowGesturesIn(IEnumerable<object?> items)
    {
        foreach (var item in items)
        {
            if (item is not MenuItem menuItem)
                continue;
            if (GestureFor(menuItem.Command, menuItem.CommandParameter) is { } gesture)
                menuItem.InputGesture = gesture;
            ShowGesturesIn(menuItem.Items);
        }
    }

    /// <summary>Make the shortcuts work while <paramref name="root"/> (usually a Window) has focus.</summary>
    public void AttachTo(InputElement root)
        => root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        foreach (var entry in Entries)
        {
            if (!entry.Gesture.Matches(e) || !entry.Command.CanExecute(entry.Parameter))
                continue;

            entry.Command.Execute(entry.Parameter);
            e.Handled = true;
            return;
        }
    }

    private static KeyMapEntry Bind(Key key, KeyModifiers modifiers, ICommand command, object? parameter = null)
        => new(new KeyGesture(key, modifiers), command, parameter);
}
