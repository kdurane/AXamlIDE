using AXamlIDE.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The side bar's Debug tab, as VS Code's Run and Debug view: Diagnostics (Visual Studio's Diagnostic Tools graph),
/// Variables (Locals), Watch and Call Stack, as sections that open and close and share the height. The tab is there
/// while debugging (DockLayoutViewModel adds and removes it); Debug > Windows > Locals and the rest bring it to the
/// front with their section open.
/// </summary>
public sealed partial class DebugSideBarViewModel : ObservableObject, IRecipient<ShowToolTabMessage>
{
    public DebugSideBarViewModel(
        DiagnosticsViewModel diagnostics,
        LocalsViewModel locals,
        WatchViewModel watch,
        CallStackViewModel callStack,
        IMessenger messenger)
    {
        Diagnostics = diagnostics;
        Locals = locals;
        Watch = watch;
        CallStack = callStack;
        messenger.RegisterAll(this);
    }

    public DiagnosticsViewModel Diagnostics { get; }
    public LocalsViewModel Locals { get; }
    public WatchViewModel Watch { get; }
    public CallStackViewModel CallStack { get; }

    [ObservableProperty] public partial bool IsDiagnosticsExpanded { get; set; } = true;
    [ObservableProperty] public partial bool IsLocalsExpanded { get; set; } = true;
    [ObservableProperty] public partial bool IsWatchExpanded { get; set; } = true;
    [ObservableProperty] public partial bool IsCallStackExpanded { get; set; } = true;

    /// <summary>Showing one of the Debug tab's sections opens it (the dock layout brings the tab to the front).</summary>
    public void Receive(ShowToolTabMessage message)
    {
        switch (message.PanelId)
        {
            case ToolTab.Diagnostics: IsDiagnosticsExpanded = true; break;
            case ToolTab.Locals: IsLocalsExpanded = true; break;
            case ToolTab.Watch: IsWatchExpanded = true; break;
            case ToolTab.CallStack: IsCallStackExpanded = true; break;
        }
    }
}
