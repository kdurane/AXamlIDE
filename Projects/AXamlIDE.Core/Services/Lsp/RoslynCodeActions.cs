using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AXamlIDE.Services.Lsp;

/// <summary>
/// Roslyn's client-side code action commands. Roslyn expects the client to handle these itself: sending them back
/// with workspace/executeCommand fails with "Missing handler for ... roslyn.client.nestedCodeAction".
/// </summary>
public static class RoslynCodeActions
{
    /// <summary>A group of actions ("Suppress or configure issues"); the group is in the command's argument.</summary>
    public const string NestedCommand = "roslyn.client.nestedCodeAction";

    /// <summary>Fix all occurrences; the scopes on offer are its argument's fixAllFlavors.</summary>
    public const string FixAllCommand = "roslyn.client.fixAllCodeAction";

    public static string? CommandName(LspCodeAction action)
        => action.Command is { ValueKind: JsonValueKind.Object } command
           && command.TryGetProperty("command", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

    /// <summary>The command's first argument: Roslyn's data for a nested or fix-all action.</summary>
    public static JsonElement? CommandData(LspCodeAction action)
        => action.Command is { ValueKind: JsonValueKind.Object } command
           && command.TryGetProperty("arguments", out var arguments)
           && arguments.ValueKind == JsonValueKind.Array && arguments.GetArrayLength() > 0
            ? arguments[0]
            : null;

    /// <summary>The action's own data (what codeAction/resolve and resolveFixAll take back).</summary>
    public static JsonElement? Data(LspCodeAction action) => Property(action.Raw, "data");

    /// <summary>The actions a nested action groups, or empty for an ordinary action.</summary>
    public static IReadOnlyList<LspCodeAction> Nested(LspCodeAction action)
    {
        if (CommandName(action) != NestedCommand
            || CommandData(action) is not { } data
            || Property(data, "nestedCodeActions") is not { ValueKind: JsonValueKind.Array } nested)
            return [];
        return [.. nested.EnumerateArray().Select(LspClient.ParseCodeAction).OfType<LspCodeAction>()];
    }

    /// <summary>
    /// Where a nested action goes in the menu under its group: ["Configure CA1822 severity", "Warning"].
    /// From Roslyn's codeActionPath, without the group's own title; just the action's title when there's no path.
    /// </summary>
    public static IReadOnlyList<string> MenuPath(LspCodeAction nested, string groupTitle)
    {
        if (Data(nested) is { } data && Property(data, "codeActionPath") is { ValueKind: JsonValueKind.Array } path)
        {
            var segments = path.EnumerateArray()
                .Where(segment => segment.ValueKind == JsonValueKind.String)
                .Select(segment => segment.GetString()!)
                .ToList();
            if (segments.Count > 0 && segments[0] == groupTitle)
                segments.RemoveAt(0);
            if (segments.Count > 0)
                return segments;
        }
        return [nested.Title];
    }

    /// <summary>The scopes a fix-all action offers ("Document", "Project", "Solution"); empty when it isn't one.</summary>
    public static IReadOnlyList<string> FixAllFlavors(JsonElement? data)
        => data is { } value && Property(value, "fixAllFlavors") is { ValueKind: JsonValueKind.Array } flavors
            ? [.. flavors.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!)]
            : [];

    /// <summary>The identifier codeAction/resolveFixAll wants as the title.</summary>
    public static string? UniqueIdentifier(JsonElement data)
        => Property(data, "uniqueIdentifier") is { ValueKind: JsonValueKind.String } id ? id.GetString() : null;

    /// <summary>A property by name, ignoring case (Roslyn's data has been both camelCase and PascalCase).</summary>
    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }
}
