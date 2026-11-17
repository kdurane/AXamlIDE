using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Services.Agents;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// Signing in, like VS Code's Claude panel: when the agent says it isn't signed in (at start-up, or a message fails
/// because the login expired) the panel shows a button for each way the agent offers (Claude subscription, Anthropic
/// Console). A click runs that sign-in command out of sight (<see cref="AgentSignIn"/>); it opens the browser, and when
/// the user has signed in there the agent is restarted and the conversation reopened where it was.
/// </summary>
public sealed partial class AgentChatViewModel
{
    private const string NotSignedInReason =
        "Sign in to use Claude. Claude Code can use your Claude subscription, or be billed for what it uses through your Anthropic Console account.";

    private const string ExpiredReason =
        "Your Claude sign-in has expired. Sign in again to carry on; the conversation reopens where it was.";

    private AgentSignIn? _signIn;

    // The conversation to reopen once signed in: the one the expired login interrupted.
    private AcpSessionInfo? _resumeAfterSignIn;

    /// <summary>A button for each: "Claude Subscription", "Anthropic Console".</summary>
    public ObservableCollection<SignInOption> SignInMethods { get; } = [];

    /// <summary>The agent offers no sign-in command: say how to do it by hand instead.</summary>
    public bool HasNoSignInMethods => SignInMethods.Count == 0;

    public string ManualSignInText
        => $"Run \"{ClaudeAgentInstall.FindCli() ?? "claude"}\" auth login in a terminal, then restart Claude.";

    /// <summary>Why sign-in is needed (not signed in, or the login expired).</summary>
    [ObservableProperty]
    public partial string SignInReason { get; set; } = NotSignedInReason;

    /// <summary>A sign-in command is running: the user is signing in in the browser.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(SubmitSignInCodeCommand))]
    public partial bool IsWaitingForBrowser { get; set; }

    /// <summary>The sign-in page to open by hand if the browser didn't open (it then shows a code to paste).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInLink))]
    public partial string? SignInLink { get; set; }

    public bool HasSignInLink => SignInLink is not null;

    /// <summary>The code from the sign-in page, pasted when it was opened by hand.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitSignInCodeCommand))]
    public partial string SignInCode { get; set; } = "";

    /// <summary>Why the last try didn't sign in; empty when there's nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInProblem))]
    public partial string SignInProblem { get; set; } = "";

    public bool HasSignInProblem => SignInProblem.Length > 0;

    private void OnAuthStateChanged(string state)
    {
        if (state == "unauthenticated")
            NeedSignIn(NotSignedInReason);
    }

    /// <summary>Show the sign-in buttons instead of the conversation (which is kept, to reopen afterwards).</summary>
    private void NeedSignIn(string reason)
    {
        if (_client is not { } client || State is not (AgentChatState.Ready or AgentChatState.SignIn))
            return;
        SignInReason = reason;
        if (State == AgentChatState.SignIn)
            return;

        _resumeAfterSignIn = _session is { } session && Items.OfType<UserMessageItem>().Any()
            ? new AcpSessionInfo(session.Id, Title == "Claude" ? null : Title, null)
            : null;
        SignInMethods.Clear();
        foreach (var method in client.AuthMethods)
            SignInMethods.Add(new SignInOption(method, IsPrimary: SignInMethods.Count == 0));
        OnPropertyChanged(nameof(HasNoSignInMethods));
        SignInProblem = "";
        SignInLink = null;
        SignInCode = "";
        State = AgentChatState.SignIn;
    }

    /// <summary>A sign-in button: run its command (which opens the browser), then restart signed in.</summary>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignIn(SignInOption option)
    {
        SignInProblem = "";
        SignInLink = null;
        SignInCode = "";

        AgentSignIn signIn;
        try
        {
            signIn = AgentSignIn.Start(option.Method);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SignInProblem = "Couldn't start the sign-in: " + ex.Message;
            return;
        }
        _signIn = signIn;
        signIn.LinkFound += link => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(signIn, _signIn))
                SignInLink = link;
        });
        IsWaitingForBrowser = true;

        var signedIn = await signIn.Completion;
        if (!ReferenceEquals(signIn, _signIn))
            return;   // cancelled, or the panel closed
        _signIn = null;
        IsWaitingForBrowser = false;
        SignInLink = null;
        var output = signIn.RecentOutput;
        signIn.Dispose();

        if (!signedIn)
        {
            SignInProblem = "The sign-in didn't finish." + (output.Length > 0 ? $" [{output}]" : "");
            return;
        }
        await RestartAfterSignInAsync();
    }

    private bool CanSignIn(SignInOption? option) => State == AgentChatState.SignIn && !IsWaitingForBrowser && option is not null;

    /// <summary>The sign-in page was opened by hand: send the code it showed to the waiting command.</summary>
    [RelayCommand(CanExecute = nameof(CanSubmitSignInCode))]
    private void SubmitSignInCode()
    {
        _signIn?.SubmitCode(SignInCode);
        SignInCode = "";
    }

    private bool CanSubmitSignInCode() => IsWaitingForBrowser && !string.IsNullOrWhiteSpace(SignInCode);

    /// <summary>Stop waiting for the browser; the sign-in buttons come back.</summary>
    [RelayCommand]
    private void CancelSignIn()
    {
        var signIn = _signIn;
        _signIn = null;
        signIn?.Dispose();
        IsWaitingForBrowser = false;
        SignInLink = null;
        SignInCode = "";
    }

    /// <summary>
    /// Signed in: the running agent still thinks it isn't, so start a fresh one, and reopen the conversation the
    /// expired login interrupted (the message that failed is still in the prompt box, ready to send again).
    /// </summary>
    private async Task RestartAfterSignInAsync()
    {
        var resume = _resumeAfterSignIn;
        _resumeAfterSignIn = null;
        DropClient();
        ClearConversation();
        await StartAsync();
        if (State != AgentChatState.Ready)
            return;   // still not signed in (back on the sign-in buttons), or it didn't start
        _messenger.Send(new StatusMessage("Signed in to Claude."));
        if (resume is not null)
            await OpenSessionAsync(resume);
    }
}

/// <summary>A sign-in button: the way to sign in, and whether it's the main one (the accent button, as VS Code's).</summary>
public sealed record SignInOption(AcpAuthMethod Method, bool IsPrimary);
