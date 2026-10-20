using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>Profile settings; closes with true on Save (DataContext: <see cref="PublishSettingsViewModel"/>).</summary>
public partial class PublishSettingsWindow : Window
{
    public PublishSettingsWindow() => InitializeComponent();

    private PublishSettingsViewModel? ViewModel => DataContext as PublishSettingsViewModel;

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the publish folder",
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder)
            ViewModel?.UsePickedFolder(folder);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.CanSave == true)
            Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
