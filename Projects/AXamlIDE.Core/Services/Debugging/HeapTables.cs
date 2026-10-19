using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace AXamlIDE.Services.Debugging;

/// <summary>Which collection to find on the heap: its type (as the debug adapter names it) and what makes it unique.</summary>
/// <param name="Count">Items in it.</param>
/// <param name="Version">Its _version (List, Dictionary, HashSet, Queue, Stack, and a wrapper's inner list), when it has one.</param>
public sealed record HeapFingerprint(string Type, int Count, int? Version);

/// <summary>
/// Reads a collection straight from the paused debuggee's memory with ClrMD (Microsoft.Diagnostics.Runtime, MIT), for
/// the table visualizer: netcoredbg reads one object per request (~2-4 ms each, 40 s for 8,000), ClrMD reads the heap
/// directly (8,000 objects in ~0.1 s). Visual Studio's engine reads fields from memory the same way.
///
/// The process is held paused by netcoredbg, so its heap is stable; ClrMD attaches without suspending (it reads the
/// memory, it doesn't trace), and netcoredbg carries on as before. There's no object address in DAP, so the
/// collection is found on the heap by type, count and version (<see cref="HeapFingerprint"/>); if that isn't exactly
/// one object, nothing is returned and the visualizer reads through the debugger instead.
///
/// Columns are what's in memory: public fields and auto-properties (their backing fields); computed properties have no
/// field, so they aren't shown. Cells are typed: numbers, bool, char, string, enums (by name), DateTime, DateTimeOffset,
/// TimeSpan, Guid, decimal, nullables; collections as "Count = n"; other objects as their first few values.
/// </summary>
public static partial class HeapTables
{
    /// <summary>The table, or null when the collection can't be found (or read) this way.</summary>
    public static TypedTable? TryRead(int processId, HeapFingerprint fingerprint, int maxRows, CancellationToken ct = default)
    {
        try
        {
            using var target = DataTarget.AttachToProcess(processId, suspend: false);
            if (target.ClrVersions.FirstOrDefault() is not { } clr)
                return null;
            using var runtime = CreateRuntime(clr);

            var wanted = Normalize(ClrName(fingerprint.Type));
            var found = new List<ClrObject>(2);
            foreach (var candidate in runtime.Heap.EnumerateObjects())
            {
                if (candidate.Type is not { } type || Normalize(type.Name) != wanted || !Matches(candidate, fingerprint))
                    continue;
                found.Add(candidate);
                if (found.Count > 1)
                    return null;   // two alike: can't tell which one is meant
                ct.ThrowIfCancellationRequested();
            }
            return found.Count == 1 ? Read(found[0], fingerprint.Count, maxRows, ct) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Can't attach (permissions, another OS), no DAC, a layout we don't know...: the slow way still works.
            return null;
        }
    }

    /// <summary>ClrMD checks that the DAC matches the runtime build; distro-built runtimes (Fedora's) fail that check.</summary>
    private static ClrRuntime CreateRuntime(ClrInfo clr)
    {
        try
        {
            return clr.CreateRuntime();
        }
        catch (Exception) when (Path.GetDirectoryName(clr.ModuleInfo.FileName) is { } folder)
        {
            var dac = Path.Combine(folder, OperatingSystem.IsWindows() ? "mscordaccore.dll" : OperatingSystem.IsMacOS() ? "libmscordaccore.dylib" : "libmscordaccore.so");
            return clr.CreateRuntime(dac, ignoreMismatch: true);
        }
    }

    // ---------------------------------------------------------------- finding the collection

    private static bool Matches(ClrObject candidate, HeapFingerprint fingerprint)
    {
        if (candidate.IsArray)
            return candidate.AsArray().Length == fingerprint.Count;
        var type = candidate.Type!;
        var inner = InnerList(candidate);
        var count = inner is { } list ? list.ReadField<int>("_size") : Count(candidate);
        if (count != fingerprint.Count)
            return false;
        if (fingerprint.Version is not { } version)
            return true;
        var holder = inner ?? candidate;
        return holder.Type?.GetFieldByName("_version") is not null && holder.ReadField<int>("_version") == version;
    }

    /// <summary>A wrapper's (Collection&lt;T&gt;, ObservableCollection&lt;T&gt;, ReadOnlyCollection&lt;T&gt;, AvaloniaList&lt;T&gt;) List.</summary>
    private static ClrObject? InnerList(ClrObject wrapper)
    {
        foreach (var name in CollectionShapes.WrapperFields)
            if (wrapper.Type?.GetFieldByName(name) is not null && wrapper.ReadObjectField(name) is { IsNull: false } inner
                && inner.Type?.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) == true)
                return inner;
        return null;
    }

    private static int Count(ClrObject collection)
    {
        var type = collection.Type!;
        if (type.GetFieldByName("_size") is not null)
            return collection.ReadField<int>("_size");
        if (type.GetFieldByName("_count") is not null)
            return collection.ReadField<int>("_count") - (type.GetFieldByName("_freeCount") is not null ? collection.ReadField<int>("_freeCount") : 0);
        return -1;
    }

    // ---------------------------------------------------------------- reading the items

    private static TypedTable? Read(ClrObject collection, int total, int maxRows, CancellationToken ct)
    {
        var items = Items(collection, maxRows);
        if (items is null)
            return null;

        var columns = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<Dictionary<int, object?>>(items.Count);
        foreach (var (name, key, value) in items)
        {
            ct.ThrowIfCancellationRequested();
            var cells = new Dictionary<int, object?>();
            void Set(string column, object? cell)
            {
                if (!index.TryGetValue(column, out var at))
                {
                    index[column] = at = columns.Count;
                    columns.Add(column);
                }
                cells[at] = cell;
            }

            if (key is { } k)
                Set("Key", Cell(Resolve(k)));
            var resolved = Resolve(value);
            if (resolved.Value is not { } item || IsSimple(resolved.Type) || TypeOf(item) is not { } itemType || itemType.IsArray)
                Set("Value", resolved.Value is null ? null : Cell(resolved));
            else
                foreach (var field in Members(itemType))
                    Set(ColumnName(field), Cell(new Slot(item, field)));
            rows.Add(cells);
        }

        var table = rows.Select((cells, i) =>
        {
            var row = new object?[columns.Count + 1];
            row[0] = (long)i;
            foreach (var (at, cell) in cells)
                row[at + 1] = cell;
            return row;
        }).ToList();
        return new TypedTable(["#", .. columns], table, "read from memory", total);
    }

    /// <summary>A cell's source: a field of an object or struct (<see cref="Value"/> is a ClrObject or ClrValueType), or an array element.</summary>
    private readonly record struct Slot(object? Value, ClrInstanceField? Field = null, ClrType? ElementType = null)
    {
        public ClrType? Type => Field?.Type ?? ElementType ?? TypeOf(Value);
    }

    /// <summary>
    /// A field slot (a dictionary entry's key / value) as the value it holds, with its real type: shared generic code
    /// gives the field a placeholder type (System.__Canon), so the object itself has to be looked at.
    /// </summary>
    private static Slot Resolve(Slot slot)
    {
        if (slot is not { Field: { } field, Value: { } owner })
            return slot;
        if (field.IsObjectReference)
        {
            var reference = ReadObject(owner, field.Name!);
            return reference.IsNull ? new Slot(null, ElementType: field.Type) : new Slot(reference, ElementType: reference.Type);
        }
        return slot;   // a primitive or struct: Cell reads it through the field
    }

    // ClrObject and ClrValueType read fields the same way but share no usable interface: these switch between them.
    private static ClrType? TypeOf(object? value) => value switch { ClrObject o => o.Type, ClrValueType v => v.Type, _ => null };

    private static T Read<T>(object owner, string field) where T : unmanaged
        => owner is ClrObject o ? o.ReadField<T>(field) : ((ClrValueType)owner).ReadField<T>(field);

    private static ClrObject ReadObject(object owner, string field)
        => owner is ClrObject o ? o.ReadObjectField(field) : ((ClrValueType)owner).ReadObjectField(field);

    private static ClrValueType ReadStruct(object owner, string field)
        => owner is ClrObject o ? o.ReadValueTypeField(field) : ((ClrValueType)owner).ReadValueTypeField(field);

    /// <summary>The items as (index name, key, value), in the order VS shows them; null for a shape we don't read.</summary>
    private static List<(string Name, Slot? Key, Slot Value)>? Items(ClrObject collection, int maxRows)
    {
        if (collection.IsArray)
            return ArrayItems(collection.AsArray(), 0, collection.AsArray().Length, maxRows);

        var type = collection.Type!;
        var name = type.Name ?? "";
        var list = InnerList(collection) ?? (name.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) ? collection : (ClrObject?)null);
        if (list is { } l)
            return ArrayItems(l.ReadObjectField("_items").AsArray(), 0, l.ReadField<int>("_size"), maxRows);

        if (name.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal)
            || name.StartsWith("System.Collections.Generic.HashSet<", StringComparison.Ordinal))
        {
            var entries = collection.ReadObjectField("_entries");
            if (entries.IsNull)
                return [];
            var array = entries.AsArray();
            var used = collection.ReadField<int>("_count");
            var items = new List<(string, Slot?, Slot)>();
            for (var i = 0; i < used && items.Count < maxRows; i++)
            {
                var entry = array.GetStructValue(i);
                var entryType = entry.Type!;
                var next = entryType.GetFieldByName("next") ?? entryType.GetFieldByName("Next");
                if (next is not null && entry.ReadField<int>(next.Name!) < -1)
                    continue;   // a freed entry
                var key = entryType.GetFieldByName("key");
                var value = entryType.GetFieldByName("value") ?? entryType.GetFieldByName("Value");
                if (value is null)
                    return null;
                items.Add(($"[{items.Count}]", key is null ? null : new Slot(entry, key), new Slot(entry, value)));
            }
            return items;
        }

        if (name.StartsWith("System.Collections.Generic.Queue<", StringComparison.Ordinal))
        {
            var array = collection.ReadObjectField("_array").AsArray();
            var head = collection.ReadField<int>("_head");
            var size = collection.ReadField<int>("_size");
            var items = new List<(string, Slot?, Slot)>();
            for (var i = 0; i < size && i < maxRows; i++)
                items.Add(($"[{i}]", null, Element(array, (head + i) % array.Length)));
            return items;
        }

        if (name.StartsWith("System.Collections.Generic.Stack<", StringComparison.Ordinal))
        {
            var array = collection.ReadObjectField("_array").AsArray();
            var size = collection.ReadField<int>("_size");
            var items = new List<(string, Slot?, Slot)>();
            for (var i = 0; i < size && i < maxRows; i++)
                items.Add(($"[{i}]", null, Element(array, size - 1 - i)));   // top first
            return items;
        }
        return null;
    }

    private static List<(string, Slot?, Slot)> ArrayItems(ClrArray array, int start, int count, int maxRows)
    {
        var items = new List<(string, Slot?, Slot)>(Math.Min(count, maxRows));
        if (array.Rank != 1)
            return items;
        for (var i = start; i < count && items.Count < maxRows; i++)
            items.Add(($"[{i}]", null, Element(array, i)));
        return items;
    }

    /// <summary>An array element: an object reference, or (primitives and structs) a struct value we read fields of.</summary>
    private static Slot Element(ClrArray array, int index)
    {
        var elementType = array.Type.ComponentType;
        if (elementType is null || elementType.IsObjectReference)
        {
            var item = array.GetObjectValue(index);
            return new Slot(item.IsNull ? null : item, ElementType: item.IsNull ? elementType : item.Type);
        }
        return new Slot(array.GetStructValue(index), ElementType: elementType);
    }

    // ---------------------------------------------------------------- columns

    /// <summary>The members shown as columns: public fields and auto-properties, in declaration order.</summary>
    private static IEnumerable<ClrInstanceField> Members(ClrType type)
        => type.Fields
            .Where(field => field.Name is { } name
                            && (BackingField().IsMatch(name) || (field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public))
            .OrderBy(field => field.Token);

    [GeneratedRegex(@"^<(?<name>[^>]+)>k__BackingField$")]
    private static partial Regex BackingField();

    private static string ColumnName(ClrInstanceField field)
        => BackingField().Match(field.Name!) is { Success: true } match ? match.Groups["name"].Value : field.Name!;

    private static bool IsSimple(ClrType? type)
        => type is null || type.IsPrimitive || type.IsString || type.IsEnum || type.Name is { } name && KnownStructs.Contains(name)
           || type.Name?.StartsWith("System.Nullable<", StringComparison.Ordinal) == true;

    private static readonly HashSet<string> KnownStructs =
        ["System.DateTime", "System.DateTimeOffset", "System.TimeSpan", "System.Guid", "System.Decimal", "System.DateOnly", "System.TimeOnly"];

    // ---------------------------------------------------------------- cells

    /// <summary>A slot's value, typed for the grid.</summary>
    private static object? Cell(Slot slot, int depth = 0)
    {
        var type = slot.Type;
        if (type is null)
            return null;

        // A field of an object / struct.
        if (slot.Field is { } field && slot.Value is { } owner)
        {
            if (field.IsObjectReference)
            {
                var reference = ReadObject(owner, field.Name!);
                return reference.IsNull ? null : ObjectCell(reference, depth);
            }
            if (field.IsPrimitive || type.IsEnum)
                return Primitive(type, type.IsEnum ? type.AsEnum().ElementType : field.ElementType, kind => ReadPrimitive(owner, field.Name!, kind));
            return StructCell(ReadStruct(owner, field.Name!), type, depth);
        }

        // An array element or the item itself.
        if (slot.Value is not { } value)
            return null;
        if (value is ClrObject obj && !obj.IsBoxedValue)
            return ObjectCell(obj, depth);
        if (type.IsPrimitive || type.IsEnum)
            return Primitive(type, type.IsEnum ? type.AsEnum().ElementType : type.ElementType, _ => ReadBoxedPrimitive(value, type));
        return StructCell(value, type, depth);
    }

    private static object? ObjectCell(ClrObject reference, int depth)
    {
        var type = reference.Type;
        if (type is null)
            return null;
        if (type.IsString)
            return reference.AsString(1000);
        if (reference.IsArray)
            return $"{{{Short(type.ComponentType?.Name)}[{reference.AsArray().Length}]}}";
        if (reference.IsBoxedValue)
            return IsSimple(type) ? Cell(new Slot(reference, ElementType: type), depth) : $"{{{Short(type.Name)}}}";
        var count = InnerList(reference) is { } inner ? inner.ReadField<int>("_size") : Count(reference);
        if (count >= 0 && (type.Name?.StartsWith("System.Collections.", StringComparison.Ordinal) == true || InnerList(reference) is not null))
            return $"Count = {count}";
        return depth > 0 ? $"{{{Short(type.Name)}}}" : Summary(reference, type);
    }

    /// <summary>A nested object as its first few simple values: {City = London, Street = 1 Main St}.</summary>
    private static string Summary(object value, ClrType type)
    {
        var parts = new List<string>();
        foreach (var field in Members(type))
        {
            if (parts.Count == 3)
            {
                parts.Add("...");
                break;
            }
            var cell = Cell(new Slot(value, field), depth: 1);
            parts.Add($"{ColumnName(field)} = {Text(cell)}");
        }
        return parts.Count == 0 ? $"{{{Short(type.Name)}}}" : "{" + string.Join(", ", parts) + "}";
    }

    private static string Text(object? cell) => cell switch
    {
        null => "null",
        IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
        _ => cell.ToString() ?? ""
    };

    private static object? StructCell(object value, ClrType type, int depth)
    {
        switch (type.Name)
        {
            case "System.DateTime":
                return DateTime(Read<ulong>(value, "_dateData"));
            case "System.DateTimeOffset":
                var local = DateTime(ReadStruct(value, "_dateTime").ReadField<ulong>("_dateData"));
                var offset = TimeSpan.FromMinutes(Read<short>(value, "_offsetMinutes"));
                return new DateTimeOffset(System.DateTime.SpecifyKind(local + offset, DateTimeKind.Unspecified), offset);
            case "System.TimeSpan":
                return TimeSpan.FromTicks(Read<long>(value, "_ticks"));
            case "System.Guid":
                return new Guid(Read<int>(value, "_a"), Read<short>(value, "_b"), Read<short>(value, "_c"),
                    Read<byte>(value, "_d"), Read<byte>(value, "_e"), Read<byte>(value, "_f"), Read<byte>(value, "_g"),
                    Read<byte>(value, "_h"), Read<byte>(value, "_i"), Read<byte>(value, "_j"), Read<byte>(value, "_k"));
            case "System.Decimal":
                var flags = Read<int>(value, "_flags");
                var lo64 = Read<ulong>(value, "_lo64");
                return new decimal((int)(uint)lo64, (int)(uint)(lo64 >> 32), (int)Read<uint>(value, "_hi32"), flags < 0, (byte)((flags >> 16) & 0xFF));
            case "System.DateOnly":
                return DateOnly.FromDayNumber(Read<int>(value, "_dayNumber"));
            case "System.TimeOnly":
                return new TimeOnly(Read<long>(value, "_ticks"));
        }
        if (type.Name?.StartsWith("System.Nullable<", StringComparison.Ordinal) == true)
        {
            if (!Read<bool>(value, "hasValue"))
                return null;
            var inner = type.GetFieldByName("value");
            return inner is null ? null : Cell(new Slot(value, inner), depth);
        }
        return depth > 0 || type.Name is null ? $"{{{Short(type.Name)}}}" : Summary(value, type);
    }

    private static DateTime DateTime(ulong data)
        => new((long)(data & 0x3FFFFFFFFFFFFFFF), (DateTimeKind)(data >> 62 == 0 ? 0 : data >> 62 == 1 ? 1 : 2));

    private static object? ReadPrimitive(object owner, string field, ClrElementType kind) => kind switch
    {
        ClrElementType.Boolean => Read<bool>(owner, field),
        ClrElementType.Char => Read<char>(owner, field),
        ClrElementType.Int8 => (long)Read<sbyte>(owner, field),
        ClrElementType.UInt8 => (long)Read<byte>(owner, field),
        ClrElementType.Int16 => (long)Read<short>(owner, field),
        ClrElementType.UInt16 => (long)Read<ushort>(owner, field),
        ClrElementType.Int32 => (long)Read<int>(owner, field),
        ClrElementType.UInt32 => (long)Read<uint>(owner, field),
        ClrElementType.Int64 => Read<long>(owner, field),
        ClrElementType.UInt64 => (decimal)Read<ulong>(owner, field),
        ClrElementType.Float => (double)Read<float>(owner, field),
        ClrElementType.Double => Read<double>(owner, field),
        ClrElementType.NativeInt => Read<long>(owner, field),
        ClrElementType.NativeUInt => (decimal)Read<ulong>(owner, field),
        _ => null
    };

    /// <summary>A primitive that is the item itself (an int[] element, a boxed int): its "m_value" field.</summary>
    private static object? ReadBoxedPrimitive(object value, ClrType type)
    {
        var field = type.IsEnum ? type.GetFieldByName("value__") : type.GetFieldByName("m_value");
        var kind = type.IsEnum ? type.AsEnum().ElementType : type.ElementType;
        return field is null ? null : ReadPrimitive(value, field.Name!, kind);
    }

    /// <summary>A primitive read, as an enum's name when the type is an enum.</summary>
    private static object? Primitive(ClrType type, ClrElementType kind, Func<ClrElementType, object?> read)
    {
        var raw = read(kind);
        if (!type.IsEnum || raw is null)
            return raw;
        var number = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
        foreach (var (name, value) in type.AsEnum().EnumerateValues())
            if (value is not null && Convert.ToDecimal(value, CultureInfo.InvariantCulture) == number)
                return name;
        return raw.ToString();
    }

    // ---------------------------------------------------------------- type names

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["bool"] = "System.Boolean", ["byte"] = "System.Byte", ["sbyte"] = "System.SByte", ["char"] = "System.Char",
        ["decimal"] = "System.Decimal", ["double"] = "System.Double", ["float"] = "System.Single", ["int"] = "System.Int32",
        ["uint"] = "System.UInt32", ["long"] = "System.Int64", ["ulong"] = "System.UInt64", ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16", ["object"] = "System.Object", ["string"] = "System.String", ["nint"] = "System.IntPtr",
        ["nuint"] = "System.UIntPtr"
    };

    [GeneratedRegex(@"\b[a-z]+\b")]
    private static partial Regex Keyword();

    /// <summary>The adapter's C# type name ("List&lt;string&gt;", "int[]") as ClrMD names it ("System.String", "System.Int32[]").</summary>
    internal static string ClrName(string type)
        => Keyword().Replace(type, match => Aliases.GetValueOrDefault(match.Value, match.Value));

    /// <summary>Compare names however they're spaced and nested ("Outer+Inner" / "Outer.Inner").</summary>
    internal static string Normalize(string? name) => (name ?? "").Replace(" ", "").Replace('+', '.');

    /// <summary>"System.Collections.Generic.List&lt;MyApp.Person&gt;" -> "List&lt;Person&gt;".</summary>
    private static string Short(string? name)
    {
        if (name is null)
            return "?";
        var text = new StringBuilder();
        var segment = new StringBuilder();
        foreach (var c in name)
        {
            if (c is '<' or '>' or ',' or '[' or ']' or ' ')
            {
                text.Append(LastPart(segment.ToString())).Append(c);
                segment.Clear();
            }
            else
            {
                segment.Append(c);
            }
        }
        return text.Append(LastPart(segment.ToString())).ToString();

        static string LastPart(string part) => part.Contains('.') ? part[(part.LastIndexOf('.') + 1)..] : part;
    }
}
