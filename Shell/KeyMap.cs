using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Keymaps;
using AXamlIDE.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using Iciclecreek.Terminal;

namespace AXamlIDE.Shell;

/// <summary>One shortcut from the active keymap: its keys (two for a chord), and the command it runs.</summary>
public sealed record KeyMapEntry(Shortcut Shortcut, IReadOnlyList<KeyGesture> Strokes, IdeCommandInfo Info, ICommand Command, object? Parameter);

/// <summary>
/// The keyboard shortcuts. Three pieces work together:
///  - CommandCatalog (Core) lists the commands that can have a key, by id ("Edit.ToggleComment");
///  - a keymap file (Core, Services/Keymaps/*.json) gives each id its keys; Settings > Keyboard picks the file;
///  - this class wires each id to the real command (<see cref="WireCommands"/>) and listens for the keys.
///
/// <see cref="AttachTo"/> hooks it to a window (MainWindow, the debug toolbar, the floating tab windows), so the
/// same keys work wherever the focus is. Keys are caught on the tunnelling pass, before the text editor sees them,
/// and a key is only marked handled when its command can execute (F10 does nothing unless paused in the debugger).
/// Chords work like Visual Studio's: after Ctrl+K the status bar waits for the second key.
///
/// Every menu item shows its key from the active keymap, by its Command, or by <c>shell:KeyMap.CommandId</c> for
/// items that use Click (the editor's right-click menu). Items with neither keep the InputGesture written in XAML
/// (the editor's own Ctrl+Z, Ctrl+C...). A button with <c>shell:KeyMap.ShortcutTip</c> shows its command's key in
/// its tooltip the same way.
/// </summary>
public sealed class KeyMap
{
    /// <summary>For menu items without a Command (they use Click): which command's key to show beside it.</summary>
    public static readonly AttachedProperty<string?> CommandIdProperty =
        AvaloniaProperty.RegisterAttached<KeyMap, MenuItem, string?>("CommandId");

    public static string? GetCommandId(MenuItem item) => item.GetValue(CommandIdProperty);
    public static void SetCommandId(MenuItem item, string? value) => item.SetValue(CommandIdProperty, value);

    /// <summary>
    /// A button's tooltip, used instead of ToolTip.Tip: the key of the button's Command in the active keymap is added
    /// when it opens, as in VS Code ("Hide Panel (Ctrl+J)").
    /// </summary>
    public static readonly AttachedProperty<string?> ShortcutTipProperty =
        AvaloniaProperty.RegisterAttached<KeyMap, Button, string?>("ShortcutTip");

    public static string? GetShortcutTip(Button button) => button.GetValue(ShortcutTipProperty);
    public static void SetShortcutTip(Button button, string? value) => button.SetValue(ShortcutTipProperty, value);

    static KeyMap()
    {
        // Something to open: the key is added when it does (the keymap can change in Settings).
        ShortcutTipProperty.Changed.AddClassHandler<Button>((button, e) => ToolTip.SetTip(button, e.NewValue));
    }

    private readonly SettingsViewModel _settings;
    private readonly IMessenger _messenger;
    private readonly Dictionary<string, (ICommand Command, object? Parameter)> _commands;
    private KeyGesture? _chordFirstKey;   // the first key of a chord, while waiting for the second

    public KeyMap(MainWindowViewModel shell, PropertiesViewModel properties, SettingsViewModel settings, IMessenger messenger)
    {
        _settings = settings;
        _messenger = messenger;
        _commands = WireCommands(shell, properties);
        CheckEveryCommandIsWired();

        Entries = Build(KeymapLibrary.Get(settings.SelectedKeymap.Id));
        settings.PropertyChanged += OnSettingsChanged;

        // Every menu item, in any window or popup, shows its key when it appears.
        Control.LoadedEvent.AddClassHandler<MenuItem>((item, _) => ShowShortcut(item));
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Button>((button, _) => ShowShortcut(button));
    }

    /// <summary>The active keymap's shortcuts.</summary>
    public IReadOnlyList<KeyMapEntry> Entries { get; private set; }

    /// <summary>Every catalog command with its key in the active keymap: Search Everywhere's Actions.</summary>
    public IEnumerable<IdeAction> Actions()
        => CommandCatalog.All.Select(info =>
        {
            var (command, parameter) = _commands[info.Id];
            return new IdeAction(info, command, parameter, Entries.FirstOrDefault(e => e.Info.Id == info.Id)?.Shortcut.ToString());
        });

    /// <summary>The command behind each id in CommandCatalog. To add a command, add a line here and to the catalog.</summary>
    private static Dictionary<string, (ICommand, object?)> WireCommands(MainWindowViewModel shell, PropertiesViewModel properties)
    {
        var editor = shell.EditorCommands;
        var documents = shell.DocumentCommands;
        var run = shell.Run;
        return new Dictionary<string, (ICommand, object?)>
        {
            ["File.OpenFolder"] = (shell.Start.OpenFolderCommand, null),
            ["File.OpenSolution"] = (shell.Start.OpenSolutionCommand, null),
            ["File.NewProject"] = (shell.Start.NewProjectCommand, null),
            ["File.CloneRepository"] = (shell.Start.CloneRepositoryCommand, null),
            ["File.StartWindow"] = (shell.ShowStartWindowCommand, null),
            ["File.Save"] = (documents.SaveDocumentCommand, null),     // null parameter = the selected tab
            ["File.SaveAs"] = (documents.SaveDocumentAsCommand, null),
            ["File.SaveAll"] = (documents.SaveAllCommand, null),
            ["File.Close"] = (documents.CloseDocumentCommand, null),
            ["File.CloseAll"] = (documents.CloseAllCommand, null),
            ["File.NewItem"] = (shell.Explorer.NewItemCommand, null),   // into the explorer selection
            ["File.NewClass"] = (shell.Explorer.NewClassCommand, null),
            ["File.NewFolder"] = (shell.Explorer.NewFolderCommand, null),

            ["Edit.ToggleComment"] = (editor.ToggleComment, null),
            ["Edit.CommentSelection"] = (editor.CommentSelection, null),
            ["Edit.UncommentSelection"] = (editor.UncommentSelection, null),
            ["Edit.FormatDocument"] = (documents.FormatDocumentCommand, null),
            ["Edit.Find"] = (editor.Find, null),
            ["Edit.Replace"] = (editor.Replace, null),
            ["Edit.GoToLine"] = (editor.GoToLine, null),
            ["Edit.FindInFiles"] = (shell.ShowFindInFilesCommand, null),
            ["Edit.Rename"] = (editor.RenameSymbol, null),
            ["Edit.QuickActions"] = (editor.QuickActions, null),
            ["Edit.CompleteWord"] = (editor.Completion, null),
            ["Edit.ParameterInfo"] = (editor.SignatureHelp, null),
            ["Edit.InsertSnippet"] = (editor.InsertSnippet, null),

            ["View.ToggleSideBar"] = (shell.ToggleExplorerCommand, null),
            ["View.Explorer"] = (shell.ShowToolCommand, ToolTab.Explorer),
            ["View.Git"] = (shell.ShowToolCommand, ToolTab.Git),
            ["View.GitGraph"] = (shell.Git.ShowGraphCommand, null),
            ["View.Properties"] = (properties.ShowCommand, null),
            ["View.Problems"] = (shell.ShowToolCommand, ToolTab.Problems),
            ["View.Output"] = (shell.ShowToolCommand, ToolTab.Output),
            ["View.DebugConsole"] = (shell.ShowToolCommand, ToolTab.Console),
            ["View.Terminal"] = (shell.ShowTerminalCommand, null),
            ["View.TestExplorer"] = (shell.ShowToolCommand, ToolTab.Tests),
            ["View.TogglePanel"] = (shell.TogglePanelCommand, null),
            ["View.ResetWindowLayout"] = (shell.ResetWindowLayoutCommand, null),
            ["View.Claude"] = (shell.ShowClaudeCommand, null),

            ["Claude.NewChat"] = (shell.NewClaudeChatCommand, null),
            ["Git.GenerateCommitMessage"] = (shell.Git.GenerateCommitMessageCommand, null),
            ["Git.Pull"] = (shell.Git.PullCommand, null),
            ["Git.Push"] = (shell.Git.PushCommand, null),
            ["Git.Fetch"] = (shell.Git.FetchCommand, null),
            ["Git.Sync"] = (shell.Git.SyncCommand, null),
            ["Git.UndoLastCommit"] = (shell.Git.UndoLastCommitCommand, null),
            ["Git.DiscardAll"] = (shell.Git.DiscardAllCommand, null),
            ["Git.NewBranch"] = (shell.Git.NewBranchCommand, null),
            ["Git.SwitchBranch"] = (shell.Git.SwitchBranchCommand, null),
            ["Git.MergeBranch"] = (shell.Git.MergeBranchCommand, null),
            ["Git.Stash"] = (shell.Git.StashCommand, null),
            ["Git.NextChange"] = (editor.NextChange, null),
            ["Git.PreviousChange"] = (editor.PreviousChange, null),
            ["Git.RevertChange"] = (editor.RevertChange, null),
            ["Git.AcceptCurrentChange"] = (editor.AcceptCurrentChange, null),
            ["Git.AcceptIncomingChange"] = (editor.AcceptIncomingChange, null),
            ["Git.AcceptBothChanges"] = (editor.AcceptBothChanges, null),

            ["Navigate.SearchEverywhere"] = (shell.SearchEverywhere.OpenCommand, SearchEverywhereTab.All),
            ["Navigate.FindAction"] = (shell.SearchEverywhere.OpenCommand, SearchEverywhereTab.Actions),
            ["Navigate.GoToFile"] = (editor.QuickOpen, null),
            ["Navigate.GoToSymbol"] = (editor.WorkspaceSymbols, null),
            ["Navigate.GoToDefinition"] = (editor.GoToDefinition, null),
            ["Navigate.FindAllReferences"] = (editor.FindReferences, null),
            ["Navigate.Back"] = (shell.NavigateBackCommand, null),
            ["Navigate.Forward"] = (shell.NavigateForwardCommand, null),
            ["Navigate.NextProblem"] = (shell.Problems.NavigateDiagnosticCommand, true),
            ["Navigate.PreviousProblem"] = (shell.Problems.NavigateDiagnosticCommand, false),

            ["Build.Build"] = (run.BuildCommand, null),
            ["Build.Rebuild"] = (run.RebuildCommand, null),
            ["Build.Clean"] = (run.CleanCommand, null),
            ["Build.Publish"] = (shell.Publish.PublishCurrentProjectCommand, null),

            ["Debug.StartOrContinue"] = (run.StartOrContinueDebuggingCommand, null),
            ["Debug.StartWithoutDebugging"] = (run.RunProjectCommand, null),
            ["Debug.Stop"] = (run.StopRunningProjectCommand, null),
            ["Debug.Restart"] = (run.RestartDebuggingCommand, null),
            ["Debug.Pause"] = (run.PauseDebuggingCommand, null),
            ["Debug.StepOver"] = (run.StepOverCommand, null),
            ["Debug.StepInto"] = (run.StepIntoCommand, null),
            ["Debug.StepOut"] = (run.StepOutCommand, null),
            ["Debug.ToggleBreakpoint"] = (editor.ToggleBreakpoint, null),
            ["Debug.DeleteAllBreakpoints"] = (shell.DeleteAllBreakpointsCommand, null),
            ["Debug.AddWatch"] = (editor.AddWatch, null),
            ["Debug.Watch"] = (shell.ShowToolCommand, ToolTab.Watch),
            ["Debug.Locals"] = (shell.ShowToolCommand, ToolTab.Locals),
            ["Debug.CallStack"] = (shell.ShowToolCommand, ToolTab.CallStack),

            ["Test.RunAll"] = (shell.Tests.RunAllCommand, null),
            ["Test.RunFailed"] = (shell.Tests.RunFailedCommand, null),

            ["Analyze.RunCodeAnalysisOnSolution"] = (shell.StatusBar.RunCodeAnalysisCommand, null),

            ["Tools.SolutionWideAnalysis"] = (shell.StatusBar.ToggleSolutionAnalysisCommand, null),
            ["Tools.Settings"] = (shell.OpenSettingsCommand, null),
        };
    }

    /// <summary>A command in the catalog with nothing behind it (or the other way round) is a bug: fail at startup.</summary>
    private void CheckEveryCommandIsWired()
    {
        var catalog = CommandCatalog.All.Select(c => c.Id).ToHashSet();
        var missing = catalog.Except(_commands.Keys).ToList();
        var unknown = _commands.Keys.Except(catalog).ToList();
        if (missing.Count > 0 || unknown.Count > 0)
            throw new InvalidOperationException(
                $"KeyMap and CommandCatalog disagree. Not wired: {string.Join(", ", missing)}. Not in the catalog: {string.Join(", ", unknown)}.");
    }

    private List<KeyMapEntry> Build(KeymapProfile profile)
    {
        var entries = new List<KeyMapEntry>();
        foreach (var info in CommandCatalog.All)
        {
            var (command, parameter) = _commands[info.Id];
            foreach (var shortcut in profile.ShortcutsFor(info.Id))
                entries.Add(new KeyMapEntry(shortcut, [.. shortcut.Strokes.Select(ToGesture)], info, command, parameter));
        }
        return entries;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.SelectedKeymap))
            return;
        Entries = Build(KeymapLibrary.Get(_settings.SelectedKeymap.Id));
        _chordFirstKey = null;
    }

    // ============================================================ keys

    /// <summary>Make the shortcuts work while <paramref name="root"/> (usually a Window) has focus.</summary>
    public void AttachTo(InputElement root)
    {
        root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        root.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
    }

    // Double Shift (Rider's Search Everywhere): two taps of Shift on its own, close together. Typing a capital, or
    // Shift+arrows, presses another key in between, so it never counts.
    private const long DoubleShiftMilliseconds = 400;
    private bool _shiftAlone;        // Shift is down and nothing else has been pressed with it
    private long _lastShiftTap;      // when the last lone tap ended (Environment.TickCount64), 0 for none

    private void TrackShift(KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            _shiftAlone = e.KeyModifiers == KeyModifiers.Shift;   // not Ctrl+Shift...
            return;
        }
        _shiftAlone = false;
        _lastShiftTap = 0;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftShift or Key.RightShift) || !_shiftAlone)
            return;
        _shiftAlone = false;
        var now = Environment.TickCount64;
        if (_lastShiftTap != 0 && now - _lastShiftTap <= DoubleShiftMilliseconds)
        {
            _lastShiftTap = 0;
            var (command, parameter) = _commands["Navigate.SearchEverywhere"];
            if (command.CanExecute(parameter))
                command.Execute(parameter);
            return;
        }
        _lastShiftTap = now;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        TrackShift(e);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;   // a modifier on its own, e.g. Ctrl going down for the second key of a chord

        if (_chordFirstKey is null && IsInTerminal(e.Source) && BelongsToShell(e))
            return;   // Ctrl+R, Ctrl+K, Alt+B... are the shell's while the terminal has focus

        var inEditor = IsInTextEditor(e.Source);
        bool InScope(KeyMapEntry entry) => entry.Info.Scope == CommandScope.Global || inEditor;

        // The second key of a chord: run the command, or say there's no such chord (Visual Studio's message).
        if (_chordFirstKey is { } first)
        {
            _chordFirstKey = null;
            e.Handled = true;
            var chord = Entries.FirstOrDefault(entry => entry.Strokes.Count == 2
                                                        && entry.Strokes[0].Equals(first)
                                                        && entry.Strokes[1].Matches(e)
                                                        && InScope(entry));
            if (chord is not null && chord.Command.CanExecute(chord.Parameter))
            {
                Status("");
                chord.Command.Execute(chord.Parameter);
            }
            else
            {
                Status($"The key combination ({Text(first)}, {Text(e)}) is not a command.");
            }
            return;
        }

        foreach (var entry in Entries)
        {
            if (entry.Strokes.Count != 1 || !entry.Strokes[0].Matches(e) || !InScope(entry)
                || !entry.Command.CanExecute(entry.Parameter))
                continue;
            entry.Command.Execute(entry.Parameter);
            e.Handled = true;
            return;
        }

        // The first key of a chord: wait for the second.
        if (Entries.FirstOrDefault(entry => entry.Strokes.Count == 2 && entry.Strokes[0].Matches(e) && InScope(entry)) is { } started)
        {
            _chordFirstKey = started.Strokes[0];
            e.Handled = true;
            Status($"({Text(_chordFirstKey)}) was pressed. Waiting for the second key of the chord...");
        }
    }

    private static bool IsInTextEditor(object? source)
        => source is TextArea || (source as Visual)?.FindAncestorOfType<TextArea>() is not null;

    private static bool IsInTerminal(object? source)
        => source is TerminalControl || (source as Visual)?.FindAncestorOfType<TerminalControl>() is not null;

    /// <summary>
    /// Keys the terminal keeps even when a shortcut uses them, like Rider's "Override IDE shortcuts": typing, and
    /// Ctrl or Alt with a letter or digit (Ctrl+R history search, Ctrl+K cut, Alt+B back a word, Ctrl+P in readline).
    /// Function keys, Ctrl+Shift, Ctrl+Alt, Ctrl+` and Ctrl+Tab still reach the IDE, so Search Everywhere, the
    /// command palette, the debugger and moving between panels work from the terminal.
    /// </summary>
    private static bool BelongsToShell(KeyEventArgs e)
    {
        if (e.Key is >= Key.F1 and <= Key.F24)
            return false;
        var letterOrDigit = e.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9;
        return e.KeyModifiers switch
        {
            KeyModifiers.None or KeyModifiers.Shift => true,
            KeyModifiers.Control or KeyModifiers.Alt => letterOrDigit,
            _ => false
        };
    }

    private void Status(string text) => _messenger.Send(new StatusMessage(text));

    // ============================================================ menus

    /// <summary>
    /// Show the active keymap's key beside a menu item. Avalonia's menu can only show one KeyGesture, in its own
    /// spelling ("Ctrl+OemQuestion"), so the item gets the first key as its InputGesture (that makes the slot
    /// visible) and the slot's text is replaced with ours ("Ctrl+/", "Ctrl+K, Ctrl+C").
    /// </summary>
    private void ShowShortcut(MenuItem item)
    {
        var id = GetCommandId(item) ?? IdFor(item.Command, item.CommandParameter);
        if (id is null)
            return;   // not a catalog command: keep the XAML's InputGesture

        var entry = Entries.FirstOrDefault(e => e.Info.Id == id);
        item.InputGesture = entry?.Strokes[0];
        var slot = item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "PART_InputGestureText");
        if (slot is null)
            return;
        if (entry is null)
            slot.ClearValue(TextBlock.TextProperty);
        else
            slot.Text = entry.Shortcut.ToString();
    }

    private void ShowShortcut(Button button)
    {
        if (GetShortcutTip(button) is not { } tip)
            return;
        var id = IdFor(button.Command, button.CommandParameter);
        var entry = Entries.FirstOrDefault(e => e.Info.Id == id);
        ToolTip.SetTip(button, entry is null ? tip : $"{tip} ({entry.Shortcut})");
    }

    private string? IdFor(ICommand? command, object? parameter)
    {
        if (command is null)
            return null;
        foreach (var (id, wired) in _commands)
            if (ReferenceEquals(wired.Command, command) && Equals(wired.Parameter, parameter))
                return id;
        return null;
    }

    // ============================================================ key names

    /// <summary>A keymap file's key stroke as an Avalonia gesture. Core only allows keys this knows (Shortcut.NamedKeys).</summary>
    private static KeyGesture ToGesture(KeyStroke stroke)
    {
        var modifiers = KeyModifiers.None;
        if (stroke.Modifiers.HasFlag(ShortcutModifiers.Ctrl)) modifiers |= KeyModifiers.Control;
        if (stroke.Modifiers.HasFlag(ShortcutModifiers.Shift)) modifiers |= KeyModifiers.Shift;
        if (stroke.Modifiers.HasFlag(ShortcutModifiers.Alt)) modifiers |= KeyModifiers.Alt;
        if (stroke.Modifiers.HasFlag(ShortcutModifiers.Meta)) modifiers |= KeyModifiers.Meta;
        return new KeyGesture(ToKey(stroke.Key), modifiers);
    }

    /// <summary>The keys whose keymap name isn't Avalonia's Key name.</summary>
    private static readonly Dictionary<string, Key> SpecialKeys = new()
    {
        ["/"] = Key.OemQuestion, [","] = Key.OemComma, ["."] = Key.OemPeriod, ["`"] = Key.OemTilde,
        [";"] = Key.OemSemicolon, ["'"] = Key.OemQuotes, ["["] = Key.OemOpenBrackets, ["]"] = Key.OemCloseBrackets,
        ["\\"] = Key.OemPipe, ["-"] = Key.OemMinus, ["="] = Key.OemPlus, ["+"] = Key.OemPlus,
        ["Backspace"] = Key.Back,
    };

    private static Key ToKey(string key)
    {
        if (SpecialKeys.TryGetValue(key, out var special))
            return special;
        if (key.Length == 1 && char.IsDigit(key[0]))
            return Key.D0 + (key[0] - '0');   // "3" is the top-row 3, as in Alt+3
        return Enum.Parse<Key>(key, ignoreCase: true);
    }

    /// <summary>A gesture the way keymap files write it: "Ctrl+/", not Avalonia's "Ctrl+OemQuestion".</summary>
    private static string Text(KeyGesture gesture) => Text(gesture.Key, gesture.KeyModifiers);

    private static string Text(KeyEventArgs e) => Text(e.Key, e.KeyModifiers);

    private static string Text(Key key, KeyModifiers modifiers)
    {
        var name = SpecialKeys.FirstOrDefault(pair => pair.Value == key).Key
                   ?? (key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString());
        var shortcutModifiers = ShortcutModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) shortcutModifiers |= ShortcutModifiers.Ctrl;
        if (modifiers.HasFlag(KeyModifiers.Shift)) shortcutModifiers |= ShortcutModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Alt)) shortcutModifiers |= ShortcutModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Meta)) shortcutModifiers |= ShortcutModifiers.Meta;
        return new KeyStroke(shortcutModifiers, name).ToString();
    }
}
