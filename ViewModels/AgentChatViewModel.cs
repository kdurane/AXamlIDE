using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Agents;
using AXamlIDE.Services.Documents;
using AXamlIDE.Services.Workspace;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>Where the Claude panel is: what it shows instead of (or as well as) the conversation.</summary>
public enum AgentChatState
{
    /// <summary>Nothing started yet (the panel hasn't been opened).</summary>
    Idle,

    /// <summary>claude-agent-acp isn't on this computer: offer to download it.</summary>
    NotInstalled,

    Installing,
    Starting,
    Ready,

    /// <summary>It wouldn't start, or it stopped: <see cref="AgentChatViewModel.Problem"/> says why.</summary>
    Failed
}

/// <summary>
/// The Claude panel: a chat with Claude Code about the workspace, like VS Code's chat view. Claude reads and edits
/// files and runs commands in the workspace folder itself; the panel shows what it says and does, asks the user when
/// a tool needs permission, and opens the files it touches. Open tabs reload its edits from disk.
///
/// It talks to claude-agent-acp over the Agent Client Protocol (Services/Agents/AcpClient). The agent starts when the
/// panel first opens and keeps running; a conversation (session) is started in the workspace folder on the first
/// message, and again after New Chat or when another folder is opened. If the agent isn't installed the panel says
/// so and downloads it when asked (ClaudeAgentInstall): nothing is downloaded without that click.
///
/// Split across files: this one (starting, installing, the commands) and AgentChatViewModel.Turn.cs (a prompt turn:
/// sending, the agent's updates, permission requests).
/// </summary>
public sealed partial class AgentChatViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceModel _workspace;
    private readonly DocumentManager _documents;
    private readonly IMessenger _messenger;
    private AcpClient? _client;
    private AcpSession? _session;
    private string? _sessionFolder;
    private ClaudeAgentRelease? _release;
    private System.Threading.CancellationTokenSource? _installCancel;

    public AgentChatViewModel(WorkspaceModel workspace, DocumentManager documents, IMessenger messenger)
    {
        _workspace = workspace;
        _documents = documents;
        _messenger = messenger;
        workspace.WorkspaceRoots.CollectionChanged += OnWorkspaceChanged;
        documents.ActiveChanged += _ => OnPropertyChanged(nameof(ActiveFileName));
        Items.CollectionChanged += OnItemsChanged;
    }

    /// <summary>The conversation, oldest first.</summary>
    public ObservableCollection<AgentChatItem> Items { get; } = [];

    /// <summary>The session's settings as drop-downs (Mode, Model), from the agent.</summary>
    public ObservableCollection<AgentSettingItem> Settings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInstalled), nameof(IsInstalling), nameof(IsStarting), nameof(IsFailed), nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(InstallCommand), nameof(RestartCommand))]
    public partial AgentChatState State { get; set; } = AgentChatState.Idle;

    public bool IsNotInstalled => State == AgentChatState.NotInstalled;
    public bool IsInstalling => State == AgentChatState.Installing;
    public bool IsStarting => State == AgentChatState.Starting;
    public bool IsFailed => State == AgentChatState.Failed;
    public bool IsReady => State == AgentChatState.Ready;

    /// <summary>Why it failed to start or stopped (State is Failed).</summary>
    [ObservableProperty]
    public partial string Problem { get; set; } = "";

    /// <summary>"Download Claude (210 MB)" once the release is known.</summary>
    [ObservableProperty]
    public partial string InstallText { get; set; } = "Download Claude";

    /// <summary>"Downloading... 45 of 210 MB".</summary>
    [ObservableProperty]
    public partial string InstallProgress { get; set; } = "";

    /// <summary>The conversation's title from the agent, or "Claude".</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "Claude";

    /// <summary>"34k / 1M tokens": how full the context window is.</summary>
    [ObservableProperty]
    public partial string UsageText { get; set; } = "";

    /// <summary>The message being typed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string PromptText { get; set; } = "";

    /// <summary>A turn is running: the send button becomes a stop button.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    public partial bool IsTurnRunning { get; set; }

    /// <summary>Send the front tab's file with the message (VS Code's "current file" chip).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFileName))]
    public partial bool IncludeActiveFile { get; set; } = true;

    /// <summary>The front tab's file name, shown on the chip; null when no real file is open.</summary>
    public string? ActiveFileName => ActiveFilePath is { } path ? Path.GetFileName(path) : null;

    private string? ActiveFilePath => _documents.Active is { IsReadOnly: false } document && File.Exists(document.Path) ? document.Path : null;

    /// <summary>The conversation is empty: show the welcome text.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>The folder Claude works in: the workspace root, or null when no folder is open.</summary>
    private string? WorkspaceFolder => _workspace.RootDirectory;

    public bool HasWorkspace => WorkspaceFolder is not null;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(IsEmpty));

    private void OnWorkspaceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasWorkspace));
        // Claude's tools work in the session's folder: another workspace needs a new conversation.
        if (_sessionFolder is not null && _sessionFolder != WorkspaceFolder)
            NewChat();
        else if (_session is null && State == AgentChatState.Ready)
            _ = EnsureSessionAsync();   // a folder opened after the panel: its drop-downs
    }

    // ------------------------------------------------------------- starting

    /// <summary>The panel was opened: start the agent if it isn't running (the view calls this when it loads).</summary>
    public void EnsureStarted()
    {
        if (State is AgentChatState.Idle)
            _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (ClaudeAgentInstall.FindAgent() is not { } agent)
        {
            State = AgentChatState.NotInstalled;
            await LookUpReleaseAsync();
            return;
        }

        State = AgentChatState.Starting;
        try
        {
            var client = await AcpClient.StartAsync(agent, [], WorkspaceFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            client.Updated += (sessionId, update) => Dispatcher.UIThread.Post(() => OnUpdate(sessionId, update));
            client.PermissionHandler = request => Dispatcher.UIThread.InvokeAsync(() => AskPermissionAsync(request));
            client.AuthStateChanged += state => Dispatcher.UIThread.Post(() => OnAuthStateChanged(state));
            client.Exited += () => Dispatcher.UIThread.Post(() => OnExited(client));
            _client = client;
            State = AgentChatState.Ready;
            if (client.AuthState is { } auth)
                OnAuthStateChanged(auth);
            _ = EnsureSessionAsync();   // so the Mode and Model drop-downs show straight away
        }
        catch (Exception ex) when (ex is AcpException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Problem = "Claude didn't start: " + ex.Message;
            State = AgentChatState.Failed;
        }
    }

    private void OnExited(AcpClient client)
    {
        if (!ReferenceEquals(client, _client))
            return;   // an old one we stopped on purpose
        var log = client.RecentLog;
        DropClient();
        Problem = "Claude stopped." + (log.Length > 0 ? $" [{log}]" : "");
        State = AgentChatState.Failed;
    }

    private void OnAuthStateChanged(string state)
    {
        if (state == "unauthenticated")
            Items.Add(new NoticeItem(
                $"Claude isn't signed in. Run \"{ClaudeAgentInstall.FindCli() ?? "claude"}\" in a terminal and sign in (/login), then start a New Chat.",
                isError: true));
    }

    /// <summary>Stop the agent and start it again (the Failed state's button; also after installing).</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task Restart()
    {
        DropClient();
        ClearConversation();
        await StartAsync();
    }

    private bool CanRestart() => State is AgentChatState.Failed or AgentChatState.Ready;

    private void DropClient()
    {
        CancelPendingPermissions();
        var client = _client;
        _client = null;
        _session = null;
        _sessionFolder = null;
        Settings.Clear();
        IsTurnRunning = false;
        client?.Dispose();
    }

    // ------------------------------------------------------------- installing

    /// <summary>Find out what would be downloaded, for the button's text. Only asks GitHub; downloads nothing.</summary>
    private async Task LookUpReleaseAsync()
    {
        try
        {
            _release = await ClaudeAgentInstall.FindLatestReleaseAsync();
            InstallText = $"Download Claude ({Megabytes(_release.Size)}, version {_release.Version})";
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or PlatformNotSupportedException
                                       or System.Text.Json.JsonException or TaskCanceledException)
        {
            InstallProgress = "Couldn't reach GitHub to find the download: " + ex.Message;
        }
    }

    /// <summary>The "Download Claude" button: fetch claude-agent-acp into the IDE's own folder, then start it.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        State = AgentChatState.Installing;
        _installCancel = new System.Threading.CancellationTokenSource();
        try
        {
            _release ??= await ClaudeAgentInstall.FindLatestReleaseAsync(_installCancel.Token);
            var release = _release;
            InstallProgress = "Downloading...";
            var progress = new Progress<long>(done => InstallProgress = $"Downloading... {Megabytes(done)} of {Megabytes(release.Size)}");
            await ClaudeAgentInstall.InstallAsync(release, progress, _installCancel.Token);
            InstallProgress = "";
            _messenger.Send(new StatusMessage($"Claude {release.Version} downloaded to {ClaudeAgentInstall.InstallFolder}."));
            await StartAsync();
        }
        catch (OperationCanceledException)
        {
            InstallProgress = "Download cancelled.";
            State = AgentChatState.NotInstalled;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or PlatformNotSupportedException or InvalidDataException)
        {
            InstallProgress = "The download failed: " + ex.Message;
            State = AgentChatState.NotInstalled;
        }
        finally
        {
            _installCancel.Dispose();
            _installCancel = null;
        }
    }

    private bool CanInstall() => State == AgentChatState.NotInstalled;

    [RelayCommand]
    private void CancelInstall() => _installCancel?.Cancel();

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):0} MB";

    // ------------------------------------------------------------- focus

    private bool _focusPending;

    /// <summary>Raised when something asks for the prompt box to get focus (View > Claude); see <see cref="RequestFocus"/>.</summary>
    public event Action? FocusRequested;

    /// <summary>Put the cursor in the prompt box, now or when the view first appears. Only for the user's own command.</summary>
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

    // ------------------------------------------------------------- files

    /// <summary>Open a file Claude worked on, at the line if it said one.</summary>
    public void OpenFile(string path, int? line)
    {
        if (File.Exists(path))
            _messenger.Send(new NavigateToSearchResultMessage(new WorkspaceSearchResult(path, line ?? 1, 0, ""), ""));
        else
            _messenger.Send(new StatusMessage($"'{path}' doesn't exist (any more)."));
    }

    public void Dispose()
    {
        _workspace.WorkspaceRoots.CollectionChanged -= OnWorkspaceChanged;
        _installCancel?.Cancel();
        DropClient();
    }
}
