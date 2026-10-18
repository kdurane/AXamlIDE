using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// Menu items show their gesture with InputGesture="..." in XAML; keep those in step with this table.
/// </summary>
public sealed class KeyMap
{
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Alt = KeyModifiers.Alt;
    private const KeyModifiers None = KeyModifiers.None;

    public KeyMap(MainWindowViewModel shell)
    {
        var editor = shell.EditorCommands;
        var run = shell.Run;

        Entries =
        [
            // ---- documents
            Bind(Key.S, Ctrl, shell.DocumentCommands.SaveDocumentCommand),     // null parameter = the selected tab
            Bind(Key.W, Ctrl, shell.DocumentCommands.CloseDocumentCommand),

            // ---- navigation and search
            Bind(Key.P, Ctrl, editor.QuickOpen),
            Bind(Key.T, Ctrl, editor.WorkspaceSymbols),
            Bind(Key.F12, None, editor.GoToDefinition),
            Bind(Key.F12, Shift, editor.FindReferences),
            Bind(Key.F, Ctrl | Shift, shell.ShowFindInFilesCommand),
            Bind(Key.F8, None, shell.Problems.NavigateDiagnosticCommand, true),
            Bind(Key.F8, Shift, shell.Problems.NavigateDiagnosticCommand, false),

            // ---- editing
            Bind(Key.R, Ctrl, editor.RenameSymbol),
            Bind(Key.OemPeriod, Ctrl, editor.QuickActions),
            Bind(Key.Space, Ctrl, editor.Completion),
            Bind(Key.Space, Ctrl | Shift, editor.SignatureHelp),

            // ---- build, run, debug (Visual Studio layout). A null project = the explorer selection.
            Bind(Key.B, Ctrl | Shift, run.BuildProjectCommand),
            Bind(Key.F5, Ctrl, run.RunProjectCommand),
            Bind(Key.F5, None, run.StartOrContinueDebuggingCommand),
            Bind(Key.F5, Shift, run.StopRunningProjectCommand),
            Bind(Key.F5, Ctrl | Shift, run.RestartDebuggingCommand),
            Bind(Key.Pause, Ctrl | Alt, run.PauseDebuggingCommand),   // Ctrl+Alt+Break
            Bind(Key.F10, None, run.StepOverCommand),
            Bind(Key.F11, None, run.StepIntoCommand),
            Bind(Key.F11, Shift, run.StepOutCommand),
        ];
    }

    public IReadOnlyList<KeyMapEntry> Entries { get; }

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
