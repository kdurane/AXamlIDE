namespace AXamlIDE.Models;

/// <summary>The answer to "Move X from A to B?" (IDialogService.ConfirmMoveAsync).</summary>
public enum MoveChoice
{
    Cancel,
    Move,

    /// <summary>Move, and update the namespaces and references listed in the dialog.</summary>
    MoveAndUpdateNamespaces
}
