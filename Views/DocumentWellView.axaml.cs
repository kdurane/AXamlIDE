using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AXamlIDE.Models;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>
/// A document area (tabs, outline, editor, preview). DataContext: <see cref="DocumentWellViewModel"/>.
/// It initialises its EditorPaneView from the view model and shows the selected document in it.
/// Call <see cref="Shutdown"/> when the hosting window closes.
/// </summary>
public partial class DocumentWellView : UserControl
{
    private DocumentWellViewModel? _viewModel;

    public DocumentWellView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public void Shutdown()
    {
        if (_viewModel is not null)
            EditorPane.Shutdown();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null || DataContext is not DocumentWellViewModel viewModel)
            return;   // set once; the editor pane can only be initialised once

        _viewModel = viewModel;
        EditorPane.Initialize(viewModel.RegistryOptions, viewModel.EditorServices);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ShowSelectedDocument();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentWellViewModel.SelectedDocument))
            ShowSelectedDocument();
    }

    private void ShowSelectedDocument()
    {
        var document = _viewModel?.SelectedDocument;
        EditorPane.SetDocument(document);
        if (document is not null)
            UpdatePreviewVisibility(_viewModel!.IsPreviewVisible);
    }

    private void UpdatePreviewVisibility(bool visible)
    {
        DocumentSurface.ColumnDefinitions[1].Width = visible ? new GridLength(4) : new GridLength(0);
        DocumentSurface.ColumnDefinitions[2].Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DocumentSurface.ColumnDefinitions[2].MinWidth = visible ? 280 : 0;
        PreviewSplitter.IsVisible = visible;
        PreviewPane.IsVisible = visible;
    }

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: OpenDocument document })
            _viewModel?.Commands.CloseDocumentCommand.Execute(document);
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle && sender is Control { DataContext: OpenDocument document })
            _viewModel?.Commands.CloseDocumentCommand.Execute(document);
    }
}
