using System;

namespace AXamlIDE.Services.Debugging;

/// <summary>
/// How a collection keeps its items, so its items can be listed the way Visual Studio's debugger views (type
/// proxies) show them. netcoredbg only gives raw fields: a List's _items array is its capacity, not its count.
/// </summary>
internal enum CollectionShape
{
    /// <summary>List&lt;T&gt;: _items[0.._size).</summary>
    List,
    /// <summary>Collection&lt;T&gt; / ObservableCollection&lt;T&gt; / ReadOnlyCollection&lt;T&gt; / AvaloniaList&lt;T&gt;: a list inside.</summary>
    Wrapper,
    /// <summary>Dictionary&lt;TKey, TValue&gt;: _entries[0.._count) that are in use.</summary>
    Dictionary,
    /// <summary>HashSet&lt;T&gt;: _entries like a dictionary, with a Value.</summary>
    Set,
    /// <summary>Queue&lt;T&gt;: _array from _head, wrapping, _size items.</summary>
    Queue,
    /// <summary>Stack&lt;T&gt;: _array[0.._size), top first.</summary>
    Stack,
    /// <summary>ImmutableArray&lt;T&gt;: an array field.</summary>
    ImmutableArray,
    /// <summary>Anything else with Count and an int indexer: evaluate x[i].</summary>
    Indexed
}

internal static class CollectionShapes
{
    /// <summary>The shape for a type as the adapter names it, or null when it isn't a collection we know.</summary>
    public static CollectionShape? For(string? type)
    {
        if (type is null)
            return null;
        var key = DebuggerAttributeIndex.Key(type);
        return key switch
        {
            "System.Collections.Generic.List" => CollectionShape.List,
            "System.Collections.ObjectModel.Collection" or "System.Collections.ObjectModel.ObservableCollection"
                or "System.Collections.ObjectModel.ReadOnlyCollection" or "System.Collections.ObjectModel.ReadOnlyObservableCollection"
                or "Avalonia.Collections.AvaloniaList" => CollectionShape.Wrapper,
            "System.Collections.Generic.Dictionary" => CollectionShape.Dictionary,
            "System.Collections.Generic.HashSet" => CollectionShape.Set,
            "System.Collections.Generic.Queue" => CollectionShape.Queue,
            "System.Collections.Generic.Stack" => CollectionShape.Stack,
            "System.Collections.Immutable.ImmutableArray" => CollectionShape.ImmutableArray,
            _ => null
        };
    }

    /// <summary>The field of a <see cref="CollectionShape.Wrapper"/> that holds its list.</summary>
    public static readonly string[] WrapperFields = ["items", "list", "_inner", "_list"];

    public static bool IsArray(string? type) => type is not null && type.EndsWith(']');

    public static bool IsFramework(string? type)
        => type is not null && (type.StartsWith("System.", StringComparison.Ordinal) || type.StartsWith("Microsoft.", StringComparison.Ordinal));
}
