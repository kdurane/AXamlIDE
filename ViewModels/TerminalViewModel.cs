using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using AXamlIDE.Models;
using AXamlIDE.Services.Settings;
using AXamlIDE.Services.Terminal;
using AXamlIDE.Services.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Terminal panel (VS Code's integrated terminal; View > Terminal, Ctrl+`): the user's shell
/// (<see cref="ShellLocator"/>) in a real pseudo-terminal, so vim, less, htop, ssh and password prompts work.
///
/// The view's TerminalControl owns the running shell. This class decides what to start and where, and asks the view
/// through <see cref="LaunchRequested"/>: when the panel is first shown, after Restart, after Open in Terminal, and
/// when a key is pressed after the shell has exited. Until something has been typed, opening a workspace moves the
/// shell to its folder (as a new VS Code window would start there).
/// </summary>
public sealed partial class TerminalViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceModel _workspace;
    private readonly IEditorSettings _settings;
    private bool _focusPending;
    private bool _typed;            // the user has typed into the current shell
    private string? _launchedIn;    // the folder the current shell started in; null when none is running

    public TerminalViewModel(WorkspaceModel workspace, IEditorSettings settings)
    {
        _workspace = workspace;
        _settings = settings;
        Shell = ShellLocator.Find();
        _workspace.WorkspaceRoots.CollectionChanged += OnWorkspaceChanged;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    /// <summary>The shell program, its arguments and the name the panel's header shows ("bash", "pwsh").</summary>
    public ShellProgram Shell { get; }

    /// <summary>The editor's font (VS Code's terminal uses it too), without ligatures: they'd join "->" in output.</summary>
    public string FontFamily => EditorSettings.ResolveFontFamily(_settings.EditorFontFamily, ligatures: false);

    /// <summary>The view should start <see cref="Shell"/> in this folder, stopping any shell that's running.</summary>
    public event Action<string>? LaunchRequested;

    /// <summary>The view should clear the screen and the scrollback.</summary>
    public event Action? ClearRequested;

    /// <summary>The view should put the keyboard focus in the terminal; see <see cref="RequestFocus"/>.</summary>
    public event Action? FocusRequested;

    /// <summary>The view should stop the shell (the app is closing).</summary>
    public event Action? StopRequested;

    /// <summary>Put the keyboard focus in the terminal, now or when the view first appears.</summary>
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

    /// <summary>Clear the screen and the scrollback (the shell keeps running).</summary>
    [RelayCommand]
    private void Clear() => ClearRequested?.Invoke();

    /// <summary>Stop the shell (and anything it's running) and start a new one in the workspace folder.</summary>
    [RelayCommand]
    private void Restart() => Launch(StartFolder());

    /// <summary>The explorer's Open in Terminal: a new shell in <paramref name="folder"/> (what was running is stopped).</summary>
    public void StartIn(string folder)
    {
        if (_launchedIn is not null && !_typed && SameFolder(folder, _launchedIn))
            return;   // already there, nothing typed yet
        Launch(folder);
    }

    // ------------------------------------------------------------- the view reports

    /// <summary>The panel is showing: start the shell if none has been started.</summary>
    public void EnsureStarted()
    {
        if (_launchedIn is null)
            Launch(StartFolder());
    }

    /// <summary>The user typed into the terminal (keys sent to the shell).</summary>
    public void OnInputSent() => _typed = true;

    /// <summary>The shell has exited (the user typed <c>exit</c>, or it was stopped).</summary>
    public void OnShellExited() => _launchedIn = null;

    /// <summary>A key was pressed while no shell is running: start a new one, as VS Code does.</summary>
    public void OnKeyAfterExit() => Launch(StartFolder());

    // ------------------------------------------------------------- the shell

    private void Launch(string folder)
    {
        if (!Directory.Exists(folder))
            folder = StartFolder();
        _launchedIn = folder;
        _typed = false;
        LaunchRequested?.Invoke(folder);
    }

    private string StartFolder()
        => _workspace.RootDirectory is { } root && Directory.Exists(root)
            ? root
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static bool SameFolder(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>A workspace opened before anything was typed: start again in its folder.</summary>
    private void OnWorkspaceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_launchedIn is not null && !_typed && _workspace.RootDirectory is { } root && !SameFolder(root, _launchedIn))
            Launch(root);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IEditorSettings.EditorFontFamily))
            OnPropertyChanged(nameof(FontFamily));
    }

    public void Dispose()
    {
        _workspace.WorkspaceRoots.CollectionChanged -= OnWorkspaceChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        StopRequested?.Invoke();
    }
}
