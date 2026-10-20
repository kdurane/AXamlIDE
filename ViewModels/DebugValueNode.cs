using System.Collections.Generic;
using AXamlIDE.Messages;
using AXamlIDE.Services.Debugging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AXamlIDE.ViewModels;

/// <summary>
/// One row of a debugger value view (DataTip, Locals, Watch): a <see cref="DebugValue"/> at a depth in the tree.
/// Expanding and editing go through its <see cref="DebugValueTree"/>, which owns the rows.
/// </summary>
public sealed partial class DebugValueNode(DebugValueTree owner, DebugValue value, int level, string path) : ObservableObject
{
    public DebugValueTree Owner { get; } = owner;

    /// <summary>Depth in the tree (0 = a root), for the indentation.</summary>
    public int Level { get; } = level;

    /// <summary>"person/Home/City": remembers expansion and spots changed values across steps.</summary>
    public string Path { get; } = path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Display), nameof(Type), nameof(HasChildren), nameof(CanVisualize),
        nameof(CanEdit), nameof(IsError), nameof(IsGroup), nameof(IsMore), nameof(Expression), nameof(IsString), nameof(IsNumber),
        nameof(IsBoolean))]
    public partial DebugValue Value { get; set; } = value;

    public string Name => Value.Name;
    public string Display => Value.Display;
    public string Type => Value.Type ?? "";
    public string? Expression => Value.Expression;
    public bool HasChildren => Value.HasChildren;
    public bool CanVisualize => Value.CanVisualize;
    public bool CanEdit => Value.CanEdit;
    public bool IsError => Value.Kind == DebugValueKind.Error;
    public bool IsGroup => Value.Kind == DebugValueKind.Group;
    public bool IsMore => Value.Kind == DebugValueKind.More;

    // For colouring the value as VS Code does (strings, numbers, booleans).
    public bool IsString => Value.Kind == DebugValueKind.Text;
    public bool IsBoolean => Value.Type == "bool" && Value.Display is "true" or "false";
    public bool IsNumber => Value.Kind == DebugValueKind.Value && Value.Display.Length > 0
                            && (char.IsDigit(Value.Display[0]) || Value.Display[0] == '-' && Value.Display.Length > 1 && char.IsDigit(Value.Display[1]));

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>Children being read from the debuggee.</summary>
    [ObservableProperty] public partial bool IsLoading { get; set; }

    /// <summary>The value differs from the last stop (VS shows it in red).</summary>
    [ObservableProperty] public partial bool IsChanged { get; set; }

    [ObservableProperty] public partial bool IsEditing { get; set; }

    [ObservableProperty] public partial string EditText { get; set; } = "";

    /// <summary>Children once read (kept while collapsed).</summary>
    internal IReadOnlyList<DebugValueNode>? Children { get; set; }

    [RelayCommand]
    private System.Threading.Tasks.Task Toggle() => Owner.ToggleAsync(this);

    [RelayCommand]
    private void Visualize() => Owner.Visualize(this);

    [RelayCommand]
    private void AddWatch()
    {
        if (Expression is { } expression)
            Owner.Messenger.Send(new AddWatchMessage(expression));
    }

    [RelayCommand]
    private System.Threading.Tasks.Task CopyValue() => Owner.CopyAsync(Display);

    [RelayCommand]
    private System.Threading.Tasks.Task CopyExpression() => Owner.CopyAsync(Expression ?? Name);

    [RelayCommand]
    private void BeginEdit()
    {
        if (!CanEdit)
            return;
        EditText = Display;
        IsEditing = true;
    }

    [RelayCommand]
    private System.Threading.Tasks.Task CommitEdit() => Owner.CommitEditAsync(this);

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;
}
