using System.Collections.Generic;

namespace AXamlIDE.Services.Debugging;

public enum DebugValueKind
{
    /// <summary>A number, bool, enum, char, null...: no children.</summary>
    Value,
    /// <summary>A string (the text visualizer can show it).</summary>
    Text,
    /// <summary>An object or struct with members.</summary>
    Object,
    /// <summary>An array or collection (the table visualizer can show it).</summary>
    Collection,
    /// <summary>"Raw View", "Static members", "Non-Public members": a node that only holds others.</summary>
    Group,
    /// <summary>An evaluation that failed; <see cref="DebugValue.Display"/> is the message.</summary>
    Error,
    /// <summary>"... 2,000 more": the rest of a long collection (open the visualizer).</summary>
    More
}

/// <summary>
/// One row of a debugger view (DataTip, Locals, Watch) as Visual Studio shows it: a name, a display value
/// ("{Person Ada}", "Count = 3", "\"text\"") and, if it can be expanded, how to get its children.
/// Valid only while the debuggee stays paused at the stop it came from.
/// </summary>
/// <param name="Expression">An expression for this value (person.Home, people[3]): for Add Watch, editing and the
/// visualizers. Null when there isn't one (a HashSet item).</param>
/// <param name="Children">How to get the children (see <see cref="DebugValueProvider.ChildrenAsync"/>); null = a leaf.</param>
/// <param name="Count">Items in a collection, when known.</param>
public sealed record DebugValue(
    string Name,
    string Display,
    string? Type,
    string? Expression,
    DebugValueKind Kind,
    DebugChildren? Children,
    int? Count = null)
{
    public bool HasChildren => Children is not null;

    public bool CanVisualize => Kind is DebugValueKind.Collection || Children is ResultsChildren
                                || Kind is DebugValueKind.Text && Display != "null";

    /// <summary>Whether the value can be edited in place (simple values with an expression to assign to).</summary>
    public bool CanEdit => Expression is not null && Kind is DebugValueKind.Value or DebugValueKind.Text;
}

/// <summary>Where a <see cref="DebugValue"/>'s children come from. Opaque outside <see cref="DebugValueProvider"/>.</summary>
public abstract record DebugChildren;

/// <summary>An object's members (filtered as VS does); <paramref name="Raw"/> for a collection's Raw View.</summary>
internal sealed record MemberChildren(int Reference, string? Type, bool Raw = false) : DebugChildren;

/// <summary>An array's elements.</summary>
internal sealed record ArrayChildren(int Reference) : DebugChildren;

/// <summary>A known collection's items (List, Dictionary...), then its Raw View.</summary>
internal sealed record CollectionChildren(CollectionShape Shape, int Reference, string? Type) : DebugChildren;

/// <summary>An IEnumerable's Results View: enumerated (expression.ToArray()) only when expanded, as in VS.</summary>
internal sealed record ResultsChildren(string Expression) : DebugChildren;

/// <summary>Children already known (a group's).</summary>
internal sealed record FixedChildren(IReadOnlyList<DebugValue> Values) : DebugChildren;

/// <summary>A collection laid out as a table, for the table visualizer.</summary>
/// <param name="Columns">Column headers; the first is the index ("[0]") or, for a 2D array, the row.</param>
/// <param name="TotalCount">Items in the collection (more than <see cref="Rows"/> when it was cut short).</param>
public sealed record DebugTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, int TotalCount);
