using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AXamlIDE.Models;
using AXamlIDE.ViewModels;

namespace AXamlIDE.Views;

/// <summary>The outline drop-down. DataContext: <see cref="ViewModels.OutlineViewModel"/>.</summary>
public partial class OutlineView : UserControl
{
    private DocumentOutlineItem? _selectedWhenOpened;
    private bool _picked;
    public OutlineView()
    {
        InitializeComponent();


        Combo.DropDownOpened += (_, _) =>
    {
        _selectedWhenOpened = (DataContext as OutlineViewModel)?.SelectedItem;
        _picked = false;
    };
        // A click on an entry, or Enter, is a pick; closing it any other way (Escape, clicking elsewhere) isn't.
        // Tunnel: the ComboBox handles both itself before they'd bubble up to here.
        Combo.AddHandler(PointerReleasedEvent, (_, e) =>
            _picked |= e.Source is Visual source && source.FindAncestorOfType<ComboBoxItem>(includeSelf: true) is not null,
            RoutingStrategies.Tunnel);
        Combo.AddHandler(KeyDownEvent, (_, e) => _picked |= Combo.IsDropDownOpen && e.Key == Key.Enter, RoutingStrategies.Tunnel);
        Combo.DropDownClosed += (_, _) =>
        {
            // Picking the entry that was already selected changes nothing, so the view model hears nothing: go there again.
            if (_picked && DataContext is OutlineViewModel { SelectedItem: { } item } viewModel && ReferenceEquals(item, _selectedWhenOpened))
                viewModel.NavigateTo(item);
        };
    }
}
