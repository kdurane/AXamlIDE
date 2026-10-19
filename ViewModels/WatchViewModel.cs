using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Messages;
using AXamlIDE.Services.Debugging;
using AXamlIDE.Services.Dialogs;
using AXamlIDE.Services.Editor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The Watch window (Visual Studio's): expressions you add (typed in, or "Add Watch" from a DataTip, Locals or the
/// editor), evaluated in the selected frame at every stop. Kept for the whole session, also while running (shown
/// without values then).
/// </summary>
public sealed partial class WatchViewModel : ObservableObject, IRecipient<AddWatchMessage>
{
    private readonly DebugSnapshotTracker _debug;
    private readonly IMessenger _messenger;
    private int _version;

    public WatchViewModel(DebugSnapshotTracker debug, IMessenger messenger, IClipboardService clipboard)
    {
        _debug = debug;
        _messenger = messenger;
        Tree = new DebugValueTree(messenger, clipboard);
        debug.Changed += () => _ = RefreshAsync();
        debug.ValuesEdited += () => _ = RefreshAsync();
        messenger.RegisterAll(this);
    }

    public DebugValueTree Tree { get; }

    /// <summary>The watched expressions, in order.</summary>
    public ObservableCollection<string> Expressions { get; } = [];

    /// <summary>The "Add item to watch" box.</summary>
    [ObservableProperty] public partial string NewExpression { get; set; } = "";

    public void Receive(AddWatchMessage message)
    {
        Add(message.Expression);
        _messenger.Send(new ShowToolTabMessage(ToolTab.Watch));
    }

    [RelayCommand]
    private void AddNew()
    {
        Add(NewExpression);
        NewExpression = "";
    }

    private void Add(string expression)
    {
        expression = expression.Trim();
        if (expression.Length == 0 || Expressions.Contains(expression))
            return;
        Expressions.Add(expression);
        _ = RefreshAsync();
    }

    /// <summary>Delete a watch (its root row; Delete key or the row's menu).</summary>
    [RelayCommand]
    private void Remove(DebugValueNode? node)
    {
        if (node is not { Level: 0 } || !Expressions.Remove(node.Name))
            return;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void RemoveAll()
    {
        Expressions.Clear();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var version = ++_version;
        var values = _debug.Values;
        var roots = new List<DebugValue>();
        foreach (var expression in Expressions.ToList())
        {
            roots.Add(values is null
                ? new DebugValue(expression, "", null, expression, DebugValueKind.Value, null)   // running: no value
                : await values.EvaluateAsync(expression, "watch"));
            if (version != _version)
                return;
        }

        await Tree.SetRootsAsync(roots, values, markChanges: values is not null);
    }
}
