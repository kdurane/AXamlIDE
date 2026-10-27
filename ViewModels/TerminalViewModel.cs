using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Threading;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AXamlIDE.Services.Terminal;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Terminal panel (VS Code's integrated terminal; View > Terminal, Ctrl+`): a shell started in the workspace
/// folder. A typed line runs as a command; while one is running, typed lines are its input. The output collects
/// above, like a terminal. <see cref="ShellSession"/> says what works without a real terminal (everyday commands
/// do; vim and password prompts don't).
///
/// The shell starts when the panel is first shown (the view calls <see cref="EnsureStarted"/>), and again when
/// the next command is typed after it has exited.
/// </summary>
public sealed partial class TerminalViewModel : ObservableObject, IDisposable
{
    private const int MaximumLines = 10_000;

    private enum ChunkKind { Text, CommandFinished, Exited }

    private readonly record struct Chunk(ChunkKind Kind, string Text = "");

    private readonly WorkspaceModel _workspace;
    private readonly ConcurrentQueue<Chunk> _pending = new();
    private readonly List<string> _history = [];
    private int _historyIndex;          // _history.Count when not browsing the history
    private int _flushQueued;
    private bool _heldCarriageReturn;   // a "\r" at the end of a chunk: wait to see if "\n" follows
    private bool _commandsRun;
    private bool _focusPending;
    private ShellSession? _shell;

    public TerminalViewModel(WorkspaceModel workspace)
    {
        _workspace = workspace;
        Output.UndoStack.SizeLimit = 0;   // read-only output: nothing to undo, so don't keep it
        _workspace.WorkspaceRoots.CollectionChanged += OnWorkspaceChanged;
    }

    /// <summary>Everything the shell has printed (and the commands typed), shown read-only.</summary>
    public TextDocument Output { get; } = new();

    [ObservableProperty] public partial string InputText { get; set; } = "";

    /// <summary>"~/src/app $" (PowerShell: "PS C:\src\app>"): shown before the input box and the echoed commands.</summary>
    [ObservableProperty] public partial string Prompt { get; set; } = "$";

    /// <summary>A command is running: typed lines go to it, and Stop (Ctrl+C) interrupts it.</summary>
    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial string ShellName { get; set; } = "";

    /// <summary>Text was added to <see cref="Output"/>; the view scrolls down if it was at the bottom.</summary>
    public event Action? OutputAppended;

    /// <summary>Raised when something asks for the input box to get focus (View > Terminal); see <see cref="RequestFocus"/>.</summary>
    public event Action? FocusRequested;

    /// <summary>Put the cursor in the input box, now or when the view first appears.</summary>
    public void RequestFocus()
    {
        if (FocusRequested is null)
            _focusPending = true;
        else
            FocusRequested.Invoke();
    }

    public bool TakePendingFocusRequest()
    {
        var pending = _focusPending;
        _focusPending = false;
        return pending;
    }

    // ------------------------------------------------------------- commands

    /// <summary>Enter: run the typed line, or send it to the running command as input.</summary>
    [RelayCommand]
    private void Submit()
    {
        var line = InputText;
        InputText = "";
        _historyIndex = _history.Count;

        if (_shell is { IsBusy: true } busy)
        {
            Write(line + "\n");
            busy.WriteInput(line);
            return;
        }

        Write($"{Prompt} {line}\n");
        if (line.Trim() is "clear" or "cls")
        {
            Clear();   // the shell's clear prints codes for a real terminal; do it here
            return;
        }

        if (line.Trim().Length > 0 && (_history.Count == 0 || _history[^1] != line))
            _history.Add(line);
        _historyIndex = _history.Count;

        EnsureStarted();
        if (_shell is null)
            return;
        _commandsRun = true;
        IsBusy = true;
        _shell.Run(line);
    }

    /// <summary>Ctrl+C / the stop button: interrupt the running command.</summary>
    [RelayCommand]
    private void Interrupt()
    {
        if (_shell is null)
            return;
        Write("^C\n");
        if (_shell.InterruptStopsShell)
            StartShell(_shell.WorkingDirectory);   // Windows: a new shell in the same folder
        else
            _shell.Interrupt();
    }

    /// <summary>Ctrl+L / the clear button (or typing clear): empty the output.</summary>
    [RelayCommand]
    private void Clear()
    {
        Output.Text = "";
        _heldCarriageReturn = false;
    }

    /// <summary>Stop the shell (and anything it's running) and start a new one in the workspace folder.</summary>
    [RelayCommand]
    private void Restart()
    {
        Clear();
        StartShell(StartFolder());
    }

    /// <summary>Up arrow: the previous command typed.</summary>
    [RelayCommand]
    private void HistoryPrevious()
    {
        if (_historyIndex == 0)
            return;
        _historyIndex--;
        InputText = _history[_historyIndex];
    }

    /// <summary>Down arrow: the next command typed, then an empty line.</summary>
    [RelayCommand]
    private void HistoryNext()
    {
        if (_historyIndex >= _history.Count)
            return;
        _historyIndex++;
        InputText = _historyIndex == _history.Count ? "" : _history[_historyIndex];
    }

    // ------------------------------------------------------------- the shell

    /// <summary>Start the shell if it isn't running (the panel was shown, or it exited).</summary>
    public void EnsureStarted()
    {
        if (_shell is { HasExited: false })
            return;
        StartShell(_shell?.WorkingDirectory ?? StartFolder());
    }

    private void StartShell(string folder)
    {
        StopShell();
        if (!Directory.Exists(folder))
            folder = StartFolder();

        try
        {
            var shell = ShellSession.Start(folder);
            shell.OutputReceived += (text, _) => Enqueue(new Chunk(ChunkKind.Text, text));
            shell.CommandFinished += _ => Enqueue(new Chunk(ChunkKind.CommandFinished));
            shell.Exited += () => Enqueue(new Chunk(ChunkKind.Exited));
            _shell = shell;
            ShellName = shell.Name;
            Prompt = PromptFor(shell.WorkingDirectory);
            IsBusy = false;
        }
        catch (InvalidOperationException ex)
        {
            Write($"Couldn't start a shell: {ex.Message}\n");
        }
    }

    private void StopShell()
    {
        if (_shell is not { } shell)
            return;
        _shell = null;   // its last events are ignored (they check _shell)
        shell.Dispose();
        IsBusy = false;
    }

    private string StartFolder()
        => _workspace.RootDirectory is { } root && Directory.Exists(root)
            ? root
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>A workspace opened before any command was typed: start again in its folder (as a new terminal would).</summary>
    private void OnWorkspaceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_shell is { IsBusy: false } && !_commandsRun && _workspace.RootDirectory is { } root
            && !string.Equals(Path.GetFullPath(root), Path.GetFullPath(_shell.WorkingDirectory), StringComparison.Ordinal))
            StartShell(root);
    }

    private static string PromptFor(string folder)
    {
        if (OperatingSystem.IsWindows())
            return $"PS {folder}>";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shown = folder == home ? "~"
            : folder.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + folder[home.Length..]
            : folder;
        return $"{shown} $";
    }

    // ------------------------------------------------------------- output (shell threads -> UI thread)

    private void Enqueue(Chunk chunk)
    {
        _pending.Enqueue(chunk);
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    /// <summary>Show what arrived since the last flush, in order, as one change to the document.</summary>
    private void Flush()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        var text = new StringBuilder();
        while (_pending.TryDequeue(out var chunk))
        {
            switch (chunk.Kind)
            {
                case ChunkKind.Text:
                    text.Append(chunk.Text);
                    break;
                case ChunkKind.CommandFinished when _shell is { } shell:
                    Prompt = PromptFor(shell.WorkingDirectory);
                    IsBusy = shell.IsBusy;
                    break;
                case ChunkKind.Exited when _shell is { HasExited: true }:
                    text.Append("\n[The shell has exited. Type a command to start a new one.]\n");
                    IsBusy = false;
                    break;
            }
        }

        if (text.Length > 0)
            Write(text.ToString());
    }

    /// <summary>
    /// Add text to the output. A lone "\r" goes back to the start of the line, so progress counters ("50%\r60%")
    /// overwrite themselves as in a terminal.
    /// </summary>
    private void Write(string text)
    {
        if (_heldCarriageReturn)
        {
            text = "\r" + text;
            _heldCarriageReturn = false;
        }

        if (text.EndsWith('\r'))
        {
            _heldCarriageReturn = true;
            text = text[..^1];
        }

        var parts = text.Replace("\r\n", "\n").Split('\r');
        Output.BeginUpdate();
        try
        {
            Output.Insert(Output.TextLength, parts[0]);
            for (var i = 1; i < parts.Length; i++)
            {
                var lastLine = Output.GetLineByNumber(Output.LineCount);
                Output.Remove(lastLine.Offset, lastLine.Length);
                Output.Insert(Output.TextLength, parts[i]);
            }

            if (Output.LineCount > MaximumLines)
                Output.Remove(0, Output.GetLineByNumber(Output.LineCount - MaximumLines + 1).Offset);
        }
        finally
        {
            Output.EndUpdate();
        }

        OutputAppended?.Invoke();
    }

    public void Dispose()
    {
        _workspace.WorkspaceRoots.CollectionChanged -= OnWorkspaceChanged;
        StopShell();
    }
}
