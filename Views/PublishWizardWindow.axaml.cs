using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// The Publish wizard; closes with true when a profile can be made (DataContext: <see cref="PublishWizardViewModel"/>).
/// Code-behind runs the pickers (owned by this window) and moves the focus between the steps.
/// </summary>
public partial class PublishWizardWindow : Window
{
    public PublishWizardWindow()
    {
        InitializeComponent();
        Opened += (_, _) => TargetList.Focus();
    }

    private PublishWizardViewModel? ViewModel => DataContext as PublishWizardViewModel;

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } viewModel)
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>On to the location page: ready to type the folder.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PublishWizardViewModel.ShowsLocation) && ViewModel?.ShowsLocation == true)
            LocationBox.Focus();
    }

    private void OnTargetDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel?.NextCommand.CanExecute(null) == true)
            ViewModel.NextCommand.Execute(null);
    }

    private async void OnBrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the publish folder",
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder)
            ViewModel?.UsePickedFolder(folder);
    }

    private async void OnBrowseProfileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Profile",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Publish profiles") { Patterns = ["*.pubxml"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } file && ViewModel is { } viewModel)
            viewModel.ImportPath = file;
    }

    private void OnFinishClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Accept() == true)
            Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
