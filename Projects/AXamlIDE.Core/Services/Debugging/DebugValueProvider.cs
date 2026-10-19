using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Debugging;

/// <summary>
/// Turns netcoredbg's raw variables into what Visual Studio's DataTips, Locals and Watch show, for one stack frame
/// of one stop:
/// <list type="bullet">
/// <item>display values: [DebuggerDisplay] (read from the assemblies), "Count = 3" for collections, and an
///   overridden ToString() as "{Person Ada}"; else the adapter's "{Person}";</item>
/// <item>children: a collection's items (List, Dictionary as [key] = value, HashSet, Queue, Stack, wrappers,
///   anything with Count and an indexer) and its Raw View; objects without explicit-interface noise, broken
///   indexer "Item" rows and [DebuggerBrowsable(Never)] members; a framework type's private fields under
///   "Non-Public members"; "Static members" as a group;</item>
/// <item>a table of a collection for the visualizer.</item>
/// </list>
/// Values come in two steps: <see cref="Classify"/> (free) and <see cref="RefineAsync"/> (evaluates in the
/// debuggee: ToString, Count...). <see cref="ChildrenAsync"/> refines the first <see cref="RefineLimit"/> children.
/// </summary>
public sealed partial class DebugValueProvider(IDebugEvaluator debugger, int frameId, DebuggerAttributeIndex? attributes = null)
{
    /// <summary>Children listed for one node; the rest are a "... more" row (the visualizer shows more).</summary>
    public const int MaxChildren = 1000;

    /// <summary>Children whose display is refined (evaluated) straight away; others on request.</summary>
    public const int RefineLimit = 100;

    public int FrameId => frameId;

    // ---------------------------------------------------------------- entry points

    /// <summary>The frame's locals (netcoredbg: one "Locals" scope, `this` included), refined.</summary>
    public async Task<IReadOnlyList<DebugValue>> LocalsAsync(CancellationToken ct = default)
    {
        var locals = await debugger.GetLocalsAsync(frameId, ct);
        return await RefineAllAsync(locals.Select(local => Classify(local)).ToList(), ct);
    }

    /// <summary>Evaluate an expression (DataTip: "hover", Watch: "watch"); a failure is an <see cref="DebugValueKind.Error"/> value.</summary>
    public async Task<DebugValue> EvaluateAsync(string expression, string context, CancellationToken ct = default)
    {
        var result = await debugger.EvaluateAsync(frameId, expression, context, ct);
        if (result.Value is not { } value)
            return Error(expression, result.Error ?? "Can't evaluate.");
        return await RefineAsync(Classify(value with { Name = expression, EvaluateName = expression }), ct);
    }

    /// <summary>Assign a new value (C# text) to an editable value; the result is the value as it is now.</summary>
    public async Task<DebugValue> SetValueAsync(DebugValue value, string newValue, CancellationToken ct = default)
    {
        if (value.Expression is not { } expression)
            return Error(value.Name, "This value can't be changed.");
        var result = await debugger.SetExpressionAsync(frameId, expression, newValue, ct);
        if (result.Error is { } error)
            return value with { Display = error, Kind = DebugValueKind.Error, Children = null };
        // Re-read it, so an object assigned gets its children and display.
        var reread = await EvaluateAsync(expression, "watch", ct);
        return reread with { Name = value.Name };
    }

    public static DebugValue Error(string name, string message) => new(name, message, null, null, DebugValueKind.Error, null);

    // ---------------------------------------------------------------- classify (no requests)

    /// <summary>What a raw variable is and how to get its children, with the adapter's display.</summary>
    public DebugValue Classify(DebugVariable variable, string? name = null, string? expression = null)
    {
        name ??= variable.Name;
        expression ??= variable.EvaluateName ?? (IsIdentifier(variable.Name) ? variable.Name : null);
        var display = variable.Value;
        var type = variable.Type;

        if (variable.VariablesReference == 0 || display == "null" || !display.StartsWith('{'))
        {
            // Strings, numbers, enums (netcoredbg gives enums children; VS doesn't expand them), null.
            var kind = type == "string" && display != "null" ? DebugValueKind.Text : DebugValueKind.Value;
            return new DebugValue(name, display, type, expression, kind, null);
        }

        if (CollectionShapes.IsArray(type))
            return new DebugValue(name, display, type, expression, DebugValueKind.Collection,
                new ArrayChildren(variable.VariablesReference), ArrayLength(display));

        if (CollectionShapes.For(type) is { } shape)
            return new DebugValue(name, display, type, expression, DebugValueKind.Collection,
                new CollectionChildren(shape, variable.VariablesReference, type));

        return new DebugValue(name, display, type, expression, DebugValueKind.Object,
            new MemberChildren(variable.VariablesReference, type));
    }

    /// <summary>"{int[6]}" -> 6; "{int[3, 2]}" -> 6.</summary>
    private static int? ArrayLength(string display)
    {
        var open = display.LastIndexOf('[');
        var close = display.LastIndexOf(']');
        if (open < 0 || close < open)
            return null;
        var total = 1;
        foreach (var part in display[(open + 1)..close].Split(','))
        {
            if (!int.TryParse(part.Trim(), out var length))
                return null;
            total *= length;
        }
        return total;
    }

    // ---------------------------------------------------------------- refine (evaluates)

    /// <summary>A better display: [DebuggerDisplay], "Count = n" for a collection, or the overridden ToString().</summary>
    public async Task<DebugValue> RefineAsync(DebugValue value, CancellationToken ct = default)
    {
        if (value.Kind is not (DebugValueKind.Object or DebugValueKind.Collection) || value.Expression is not { } expression)
            return value;
        if (CollectionShapes.IsArray(value.Type))
            return value;   // "{int[6]}", as VS shows arrays

        try
        {
            if (attributes?.DisplayFormatFor(value.Type) is { } format && await FormatDisplayAsync(format, expression, ct) is { } shown)
                return value with { Display = shown, Count = value.Count ?? await CountAsync(value, expression, ct) };

            if (value.Kind == DebugValueKind.Collection || value.Children is CollectionChildren)
            {
                if (await CountAsync(value, expression, ct) is { } count)
                    return value with { Display = $"Count = {count}", Count = count };
                return value;
            }

            var text = await debugger.EvaluateAsync(frameId, $"{expression}.ToString()", "hover", ct);
            if (text.Value is { Value: var result } && Unquote(result) is { } toString && IsOverridden(toString, value.Type))
                return value with { Display = "{" + toString + "}" };
        }
        catch (DebugAdapterException)
        {
            // Keep the adapter's display.
        }
        return value;
    }

    private async Task<int?> CountAsync(DebugValue value, string expression, CancellationToken ct)
    {
        if (value.Kind != DebugValueKind.Collection)
            return null;
        var count = await debugger.EvaluateAsync(frameId, $"{expression}.Count", "hover", ct);
        return count.Value is { } result && int.TryParse(result.Value, out var n) ? n : null;
    }

    private async Task<IReadOnlyList<DebugValue>> RefineAllAsync(List<DebugValue> values, CancellationToken ct)
    {
        for (var i = 0; i < values.Count && i < RefineLimit; i++)
            values[i] = await RefineAsync(values[i], ct);
        return values;
    }

    /// <summary>
    /// Object.ToString() gives the type's name ("MyApp.Person", "System.Collections.Generic.List`1[Person]",
    /// "Outer+Inner"); anything else means the type overrides it.
    /// </summary>
    internal static bool IsOverridden(string toString, string? type)
    {
        if (type is null)
            return true;
        var typeKey = DebuggerAttributeIndex.Key(type).Replace(" ", "");
        var tick = toString.IndexOf('`');
        var name = (tick < 0 ? toString : toString[..tick]).Replace('+', '.');
        return !(name == typeKey || typeKey.EndsWith("." + name, StringComparison.Ordinal) || name.EndsWith("." + typeKey, StringComparison.Ordinal));
    }

    // {expression} or {expression,nq} in a [DebuggerDisplay] format.
    [GeneratedRegex(@"\{(?<expr>[^{},]+)(?:,(?<format>[^{}]*))?\}")]
    private static partial Regex DisplayPart();

    [GeneratedRegex(@"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*(?:\(\))?$")]
    private static partial Regex MemberChain();

    /// <summary>[DebuggerDisplay("{City}, {Street}")] -> "\"London\", \"1 Main St\"" (no quotes with ,nq). Null if a part fails.</summary>
    internal async Task<string?> FormatDisplayAsync(string format, string expression, CancellationToken ct)
    {
        var shown = new StringBuilder();
        var at = 0;
        foreach (Match part in DisplayPart().Matches(format))
        {
            shown.Append(format, at, part.Index - at);
            at = part.Index + part.Length;

            var member = part.Groups["expr"].Value.Trim();
            if (!MemberChain().IsMatch(member))
                return null;   // arbitrary expressions would be evaluated in the wrong scope
            var result = await debugger.EvaluateAsync(frameId, $"{expression}.{member}", "hover", ct);
            if (result.Value is not { } value)
                return null;
            var noQuotes = part.Groups["format"].Value.Contains("nq", StringComparison.Ordinal);
            shown.Append(noQuotes && Unquote(value.Value) is { } text ? text : value.Value);
        }
        shown.Append(format, at, format.Length - at);
        return shown.ToString();
    }

    // ---------------------------------------------------------------- text

    /// <summary>The adapter's quoted string ("\"a\\nb\"") as text ("a\nb"); null if it isn't a quoted string.</summary>
    public static string? Unquote(string display)
    {
        if (display.Length < 2 || display[0] != '"' || display[^1] != '"')
            return null;
        var text = new StringBuilder(display.Length);
        for (var i = 1; i < display.Length - 1; i++)
        {
            var c = display[i];
            if (c != '\\' || i + 1 >= display.Length - 1)
            {
                text.Append(c);
                continue;
            }
            var next = display[++i];
            switch (next)
            {
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case '0': text.Append('\0'); break;
                case 'u' when i + 4 < display.Length - 1 && int.TryParse(display.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code):
                    text.Append((char)code);
                    i += 4;
                    break;
                default: text.Append(next); break;   // \" \\ \'
            }
        }
        return text.ToString();
    }

    private static bool IsIdentifier(string name)
        => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}
