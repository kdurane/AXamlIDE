namespace AXamlIDE.ViewModels;

/// <summary>What the main document area shows when it has no tabs (see Views/WelcomeView).</summary>
public sealed class WelcomeViewModel(WorkspaceViewModel workspace)
{
    public WorkspaceViewModel Workspace { get; } = workspace;
}
