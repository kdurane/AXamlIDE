namespace AXamlIDE.ViewModels;

/// <summary>
/// The Claude chat as a document tab (Views/AgentChatView), opened by View > Claude. Like any page tab it can be
/// dragged to another tab strip or out into its own window. There's only one: the conversation lives in the
/// app-wide <see cref="AgentChatViewModel"/>, so closing the tab and opening it again brings the same chat back.
/// </summary>
public sealed class AgentChatDockable : PageDockable
{
    public const string TabId = "claude";

    public AgentChatDockable(AgentChatViewModel chat)
    {
        Chat = chat;
        Id = TabId;
        Title = "Claude";
        CanPin = false;
    }

    public AgentChatViewModel Chat { get; }

    /// <summary>Nothing to release: the chat outlives its tab (it's disposed with the app).</summary>
    public override void Dispose()
    {
    }
}
