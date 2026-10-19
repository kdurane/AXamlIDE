using System;
using AXamlIDE.Messages;
using AXamlIDE.Services.Debugging;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The DataTip for a collection (Views/CollectionDataTipView): no rows (listing thousands in a tooltip is slow and
/// unreadable), just "people  Count = 8,002  List of Person  [Visualize in table]".
/// </summary>
public sealed partial class CollectionDataTip(DebugValue value, DebugValueProvider provider, IMessenger messenger, Action close)
{
    public string Name => value.Name;

    public string CountText => value.Count is { } count ? $"Count = {count:N0}" : value.Display;

    /// <summary>"List of Person", "array of int", "Dictionary of string → Person".</summary>
    public string Description => value.Type is not { } type
        ? ""
        : DebugTypeNames.ElementType(type) is { } element
            ? $"{DebugTypeNames.CollectionKind(type)} of {element}"
            : DebugTypeNames.Short(type);

    /// <summary>The full type, for the description's tooltip.</summary>
    public string TypeText => value.Type ?? "";

    [RelayCommand]
    private void Visualize()
    {
        messenger.Send(new VisualizeDebugValueMessage(value, provider));
        close();
    }

    [RelayCommand]
    private void AddWatch()
    {
        if (value.Expression is { } expression)
            messenger.Send(new AddWatchMessage(expression));
        close();
    }
}
