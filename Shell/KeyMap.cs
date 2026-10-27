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
using AXamlIDE.Services.Keymaps;
using AXamlIDE.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

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
/// (the editor's own Ctrl+Z, Ctrl+C...).
/// </summary>
public sealed class KeyMap
{
    /// <summary>For menu items without a Command (they use Click): which command's key to show beside it.</summary>
    public static readonly AttachedProperty<string?> CommandIdProperty =
        AvaloniaProperty.RegisterAttached<KeyMap, MenuItem, string?>("CommandId");

    public static string? GetCommandId(MenuItem item) => item.GetValue(CommandIdProperty);
    public static void SetCommandId(MenuItem item, string? value) => item.SetValue(CommandIdProperty, value);

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
    }

    /// <summary>The active keymap's shortcuts.</summary>
    public IReadOnlyList<KeyMapEntry> Entries { get; private set; }

    /// <summary>The command behind each id in CommandCatalog. To add a command, add a line here and to the catalog.</summary>
    private static Dictionary<string, (ICommand, object?)> WireCommands(MainWindowViewModel shell, PropertiesViewModel properties)
    {
        var editor = shell.EditorCommands;
        var documents = shell.DocumentCommands;
        var run = shell.Run;
        return new Dictionary<string, (ICommand, object?)>
        {
            ["File.OpenFolder"] = (shell.Workspace.OpenFolderCommand, null),
            ["File.OpenSolution"] = (shell.Workspace.OpenSolutionCommand, null),
            ["File.Save"] = (documents.SaveDocumentCommand, null),     // null parameter = the selected tab
            ["File.Close"] = (documents.CloseDocumentCommand, null),
            ["File.NewItem"] = (shell.Explorer.NewItemCommand, null),   // into the explorer selection
            ["File.NewClass"] = (shell.Explorer.NewClassCommand, null),
            ["File.NewFolder"] = (shell.Explorer.NewFolderCommand, null),

            ["Edit.ToggleComment"] = (editor.ToggleComment, null),
            ["Edit.CommentSelection"] = (editor.CommentSelection, null),
            ["Edit.UncommentSelection"] = (editor.UncommentSelection, null),
            ["Edit.FormatDocument"] = (documents.FormatDocumentCommand, null),
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

            ["Navigate.GoToFile"] = (editor.QuickOpen, null),
            ["Navigate.GoToSymbol"] = (editor.WorkspaceSymbols, null),
            ["Navigate.GoToDefinition"] = (editor.GoToDefinition, null),
            ["Navigate.FindAllReferences"] = (editor.FindReferences, null),
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
            ["Debug.AddWatch"] = (editor.AddWatch, null),
            ["Debug.Watch"] = (shell.ShowToolCommand, ToolTab.Watch),
            ["Debug.Locals"] = (shell.ShowToolCommand, ToolTab.Locals),
            ["Debug.CallStack"] = (shell.ShowToolCommand, ToolTab.CallStack),

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
        => root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;   // a modifier on its own, e.g. Ctrl going down for the second key of a chord

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
