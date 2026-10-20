using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>A project's Publish page (DataContext: <see cref="PublishDockable"/>). Code-behind only opens the folder.</summary>
public partial class PublishView : UserControl
{
    public PublishView() => InitializeComponent();

    /// <summary>"Open folder": the target location in the system's file manager.</summary>
    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublishDockable { TargetLocation: { Length: > 0 } folder } page
            && Directory.Exists(folder)
            && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            await topLevel.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
            page.RefreshTargetLocation();
        }
    }
}
