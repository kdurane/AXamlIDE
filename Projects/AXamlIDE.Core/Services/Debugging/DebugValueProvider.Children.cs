using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Debugging;

public sealed partial class DebugValueProvider
{
    private const string RawView = "Raw View";
    private const string NonPublicMembers = "Non-Public members";
    private const string StaticMembers = "Static members";

    /// <summary>A value's children, the first <see cref="RefineLimit"/> refined.</summary>
    public async Task<IReadOnlyList<DebugValue>> ChildrenAsync(DebugValue value, CancellationToken ct = default)
    {
        var children = value.Children switch
        {
            FixedChildren fixedChildren => fixedChildren.Values.ToList(),
            ArrayChildren array => await ArrayItemsAsync(array.Reference, value.Expression, MaxChildren, ct),
            CollectionChildren collection => await CollectionChildrenAsync(collection, value, ct),
            MemberChildren members => await WithResultsViewAsync(await MembersAsync(members, value.Expression, ct), members, value.Expression, ct),
            ResultsChildren results => await ResultsAsync(results.Expression, ct),
            _ => []
        };
        return await RefineAllAsync(children, ct);
    }

    private const string ResultsView = "Results View";

    /// <summary>
    /// Objects that are IEnumerable&lt;T&gt; (LINQ queries, custom collections) get VS's Results View. netcoredbg has
    /// no "is", so ask the type for the interface.
    /// </summary>
    private async Task<List<DebugValue>> WithResultsViewAsync(List<DebugValue> members, MemberChildren source, string? expression, CancellationToken ct)
    {
        if (source.Raw || expression is null)
            return members;
        var check = await debugger.EvaluateAsync(frameId, $"{expression}.GetType().GetInterface(\"IEnumerable`1\")", "hover", ct);
        if (check.Value is { Value: not "null" })
            members.Add(new DebugValue(ResultsView, "Expanding the Results View will enumerate the IEnumerable", null, null,
                DebugValueKind.Group, new ResultsChildren(expression)));
        return members;
    }

    private async Task<List<DebugValue>> ResultsAsync(string expression, CancellationToken ct)
    {
        var results = await debugger.EvaluateAsync(frameId, $"{expression}.ToArray()", "hover", ct);
        if (results.Value is not { VariablesReference: > 0 } array)
            return [Error("", results.Error ?? "Enumeration yielded no results")];
        return await ArrayItemsAsync(array.VariablesReference, $"{expression}.ToArray()", MaxChildren, ct);
    }

    /// <summary>A collection's items only (no Raw View), up to <paramref name="limit"/>; for the visualizer.</summary>
    public async Task<IReadOnlyList<DebugValue>> ItemsAsync(DebugValue value, int limit, CancellationToken ct = default)
        => value.Children switch
        {
            ArrayChildren array => await ArrayItemsAsync(array.Reference, value.Expression, limit, ct),
            CollectionChildren collection => await CollectionItemsAsync(collection, value.Expression, limit, ct),
            ResultsChildren results => await ResultsAsync(results.Expression, ct),
            _ => []
        };

    // ---------------------------------------------------------------- members

    private async Task<List<DebugValue>> MembersAsync(MemberChildren source, string? expression, CancellationToken ct)
    {
        var raw = await debugger.GetVariablesAsync(source.Reference, ct);
        var members = new List<DebugValue>();
        var nonPublic = new List<DebugValue>();
        DebugValue? statics = null;
        var framework = CollectionShapes.IsFramework(source.Type);

        foreach (var variable in raw)
        {
            if (variable.Name == StaticMembers)
            {
                statics = new DebugValue(StaticMembers, "", null, null, DebugValueKind.Group,
                    variable.VariablesReference > 0 ? new MemberChildren(variable.VariablesReference, null) : null);
                continue;
            }
            if (!source.Raw && IsNoise(variable, source.Type))
                continue;

            // Built from the parent's expression (people[0].Name), not the adapter's raw path (people._items[0].Name).
            var child = Classify(variable, expression: Member(expression, variable.Name) ?? variable.EvaluateName);
            if (framework && !source.Raw && IsNonPublic(variable.Name))
                nonPublic.Add(child);
            else
                members.Add(child);
        }

        if (nonPublic.Count > 0)
            members.Add(Group(NonPublicMembers, nonPublic));
        if (statics is not null)
            members.Add(statics);
        return members;
    }

    /// <summary>Rows VS doesn't show: explicit interface members, indexers ("Item" fails with a parameter count error), hidden members.</summary>
    private bool IsNoise(DebugVariable variable, string? type)
        => variable.Name.Contains('.')
           || variable.Type == "System.Reflection.TargetParameterCountException"
           || attributes?.IsHidden(type, variable.Name) == true;

    /// <summary>Framework types' private fields (_items, _version...): VS groups them as non-public.</summary>
    private static bool IsNonPublic(string name) => name.Length > 0 && (name[0] == '_' || name[0] == '<' || char.IsLower(name[0]));

    private static string? Member(string? expression, string name) => expression is null ? null : $"{expression}.{name}";

    private static DebugValue Group(string name, IReadOnlyList<DebugValue> children)
        => new(name, "", null, null, DebugValueKind.Group, new FixedChildren(children));

    // ---------------------------------------------------------------- arrays

    private async Task<List<DebugValue>> ArrayItemsAsync(int reference, string? expression, int limit, CancellationToken ct)
    {
        var raw = await debugger.GetVariablesAsync(reference, ct);
        var items = raw.Take(limit)
            .Select(item => Classify(item, expression: expression is null ? item.EvaluateName : expression + item.Name))
            .ToList();
        AddMore(items, raw.Count, limit);
        return items;
    }

    private static void AddMore(List<DebugValue> items, int total, int shown)
    {
        if (total > shown)
            items.Add(new DebugValue($"... {total - shown:N0} more", "Open the visualizer to see them all", null, null, DebugValueKind.More, null));
    }

    // ---------------------------------------------------------------- collections

    private async Task<List<DebugValue>> CollectionChildrenAsync(CollectionChildren source, DebugValue value, CancellationToken ct)
    {
        var items = await CollectionItemsAsync(source, value.Expression, MaxChildren, ct);
        items.Add(new DebugValue(RawView, "", null, value.Expression, DebugValueKind.Group,
            new MemberChildren(source.Reference, source.Type, Raw: true)));
        return items;
    }

    private async Task<List<DebugValue>> CollectionItemsAsync(CollectionChildren source, string? expression, int limit, CancellationToken ct)
    {
        try
        {
            var fields = await debugger.GetVariablesAsync(source.Reference, ct);
            return source.Shape switch
            {
                CollectionShape.List => await ListItemsAsync(fields, expression, limit, ct),
                CollectionShape.Wrapper => await WrapperItemsAsync(fields, expression, limit, ct),
                CollectionShape.Dictionary => await DictionaryItemsAsync(fields, expression, limit, ct),
                CollectionShape.Set => await SetItemsAsync(fields, limit, ct),
                CollectionShape.Queue => await QueueItemsAsync(fields, expression, limit, ct),
                CollectionShape.Stack => await StackItemsAsync(fields, expression, limit, ct),
                CollectionShape.ImmutableArray => Field(fields, "array") is { VariablesReference: > 0 } array
                    ? await ArrayItemsAsync(array.VariablesReference, expression, limit, ct)
                    : [],
                _ => await IndexedItemsAsync(expression, Int(Field(fields, "Count")), limit, ct)
            };
        }
        catch (DebugAdapterException)
        {
            return [];   // an unexpected layout: the Raw View still shows everything
        }
    }

    private async Task<List<DebugValue>> ListItemsAsync(IReadOnlyList<DebugVariable> fields, string? expression, int limit, CancellationToken ct)
    {
        var count = Int(Field(fields, "_size")) ?? Int(Field(fields, "Count")) ?? 0;
        if (Field(fields, "_items") is not { VariablesReference: > 0 } items)
            return [];
        var raw = await debugger.GetVariablesAsync(items.VariablesReference, ct);
        return Indexed(raw.Take(count).ToList(), expression, count, limit);
    }

    private async Task<List<DebugValue>> WrapperItemsAsync(IReadOnlyList<DebugVariable> fields, string? expression, int limit, CancellationToken ct)
    {
        var inner = CollectionShapes.WrapperFields.Select(name => Field(fields, name)).FirstOrDefault(field => field is { VariablesReference: > 0 });
        if (inner is null)
            return await IndexedItemsAsync(expression, Int(Field(fields, "Count")), limit, ct);
        var innerFields = await debugger.GetVariablesAsync(inner.VariablesReference, ct);
        if (CollectionShapes.For(inner.Type) == CollectionShape.List)
            return await ListItemsAsync(innerFields, expression, limit, ct);
        return await IndexedItemsAsync(expression, Int(Field(fields, "Count")), limit, ct);
    }

    /// <summary>Items as [0], [1]... with x[i] expressions.</summary>
    private List<DebugValue> Indexed(IReadOnlyList<DebugVariable> raw, string? expression, int count, int limit)
    {
        var items = new List<DebugValue>();
        for (var i = 0; i < raw.Count && i < limit; i++)
            items.Add(Classify(raw[i], $"[{i}]", expression is null ? null : $"{expression}[{i}]"));
        AddMore(items, count, limit);
        return items;
    }

    /// <summary>Only a Count and an indexer: evaluate x[0], x[1]... one at a time.</summary>
    private async Task<List<DebugValue>> IndexedItemsAsync(string? expression, int? count, int limit, CancellationToken ct)
    {
        if (expression is null || count is not > 0)
            return [];
        var items = new List<DebugValue>();
        for (var i = 0; i < count && i < limit; i++)
        {
            var item = $"{expression}[{i}]";
            var result = await debugger.EvaluateAsync(frameId, item, "hover", ct);
            items.Add(result.Value is { } value ? Classify(value, $"[{i}]", item) : Error($"[{i}]", result.Error ?? ""));
        }
        AddMore(items, count.Value, limit);
        return items;
    }

    /// <summary>Entries in use: index below _count and next &gt;= -1 (free-list entries are lower).</summary>
    private async Task<List<IReadOnlyList<DebugVariable>>> UsedEntriesAsync(IReadOnlyList<DebugVariable> fields, int limit, CancellationToken ct)
    {
        var count = Int(Field(fields, "_count")) ?? 0;
        var used = new List<IReadOnlyList<DebugVariable>>();
        if (Field(fields, "_entries") is not { VariablesReference: > 0 } entries)
            return used;
        var raw = await debugger.GetVariablesAsync(entries.VariablesReference, ct);
        for (var i = 0; i < raw.Count && i < count && used.Count < limit; i++)
        {
            if (raw[i].VariablesReference == 0)
                continue;
            var entry = await debugger.GetVariablesAsync(raw[i].VariablesReference, ct);
            if ((Int(Field(entry, "next")) ?? -1) >= -1)
                used.Add(entry);
        }
        return used;
    }

    /// <summary>Dictionary items as VS Code / Rider show them: ["Ada"] = 36, expanding to the value's members.</summary>
    private async Task<List<DebugValue>> DictionaryItemsAsync(IReadOnlyList<DebugVariable> fields, string? expression, int limit, CancellationToken ct)
    {
        var count = Int(Field(fields, "_count")) - (Int(Field(fields, "_freeCount")) ?? 0) ?? 0;
        var items = new List<DebugValue>();
        foreach (var entry in await UsedEntriesAsync(fields, limit, ct))
        {
            if (Field(entry, "key") is not { } key || Field(entry, "value") is not { } value)
                continue;
            var keyText = key.Value;
            // A key written as a C# literal ("Ada", 3) can be indexed with; an object key ({Person}) can't.
            var keyExpression = keyText.StartsWith('{') ? null : expression is null ? null : $"{expression}[{keyText}]";
            items.Add(Classify(value, $"[{keyText}]", keyExpression ?? value.EvaluateName));
        }
        AddMore(items, count, limit);
        return items;
    }

    private async Task<List<DebugValue>> SetItemsAsync(IReadOnlyList<DebugVariable> fields, int limit, CancellationToken ct)
    {
        var count = Int(Field(fields, "_count")) - (Int(Field(fields, "_freeCount")) ?? 0) ?? 0;
        var items = new List<DebugValue>();
        foreach (var entry in await UsedEntriesAsync(fields, limit, ct))
            if (Field(entry, "value") is { } value)
                items.Add(Classify(value, $"[{items.Count}]", value.EvaluateName));
        AddMore(items, count, limit);
        return items;
    }

    private async Task<List<DebugValue>> QueueItemsAsync(IReadOnlyList<DebugVariable> fields, string? expression, int limit, CancellationToken ct)
    {
        var head = Int(Field(fields, "_head")) ?? 0;
        var size = Int(Field(fields, "_size")) ?? 0;
        if (Field(fields, "_array") is not { VariablesReference: > 0 } array)
            return [];
        var raw = await debugger.GetVariablesAsync(array.VariablesReference, ct);
        var items = new List<DebugValue>();
        for (var i = 0; i < size && i < limit && raw.Count > 0; i++)
            items.Add(Classify(raw[(head + i) % raw.Count], $"[{i}]", raw[(head + i) % raw.Count].EvaluateName));
        AddMore(items, size, limit);
        return items;
    }

    private async Task<List<DebugValue>> StackItemsAsync(IReadOnlyList<DebugVariable> fields, string? expression, int limit, CancellationToken ct)
    {
        var size = Int(Field(fields, "_size")) ?? 0;
        if (Field(fields, "_array") is not { VariablesReference: > 0 } array)
            return [];
        var raw = await debugger.GetVariablesAsync(array.VariablesReference, ct);
        var items = new List<DebugValue>();
        for (var i = 0; i < size && i < limit && size - 1 - i < raw.Count; i++)   // top of the stack first, as VS shows it
            items.Add(Classify(raw[size - 1 - i], $"[{i}]", raw[size - 1 - i].EvaluateName));
        AddMore(items, size, limit);
        return items;
    }

    private static DebugVariable? Field(IReadOnlyList<DebugVariable> fields, string name)
        => fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));

    private static int? Int(DebugVariable? variable)
        => variable is not null && int.TryParse(variable.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
