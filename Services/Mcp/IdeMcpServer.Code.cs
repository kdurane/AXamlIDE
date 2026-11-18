using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Services.Mcp;

/// <summary>
/// The language server's answers: find_symbol, inspect_symbol and find_references. Roslyn already has the solution
/// loaded, so these know what grep can't (which overload, the other halves of a partial class, generated code, the
/// type of a var). In AXAML, inspect_symbol goes through the IDE's own XAML engine: a binding's view-model property,
/// an element's type.
/// </summary>
public sealed partial class IdeMcpServer
{
    private const int MaxLocations = 100;

    /// <summary>A place in a file: where a tool was pointed (path, line and the name on it).</summary>
    private sealed record CodePoint(string Path, string Text, int Line, int Character)
    {
        public string Uri => FileUris.FromPath(Path);
        public int Offset => OffsetOf(Text, Line, Character);
    }

    /// <summary>The arguments that point at a name in a file; shared by inspect_symbol and find_references.</summary>
    private static readonly (string, string, string, string[]?)[] PointArguments =
    [
        ("path", "string", "The file (absolute or workspace-relative).", null),
        ("line", "number", "The line the name is on, from 1.", null),
        ("symbol", "string", "The name as written on that line (e.g. RenderAsync, _documents, MainViewModel).", null),
        ("column", "number", "Optional: the column (from 1) near the name, when it's on the line more than once.", null)
    ];

    // ------------------------------------------------------------ find_symbol

    private McpTool FindSymbol()
    {
        var schema = McpArguments.Schema(
            ("query", "string", "The name, or part of it (fuzzy: \"RunVM\" finds RunViewModel).", null),
            ("limit", "number", "At most this many. Default 30.", null));
        schema["required"] = new JsonArray("query");
        return new McpTool("find_symbol",
            "Find types and members by name across the whole solution (the C# language server's workspace symbols), " +
            "one per line: kind name (container) path:line. Generated code is marked.",
            schema,
            (arguments, ct) => OnUiThread(() => FindSymbolAsync(arguments, ct)));
    }

    private async Task<McpToolResult> FindSymbolAsync(JsonElement arguments, CancellationToken ct)
    {
        if (McpArguments.String(arguments, "query") is not { Length: > 0 } query)
            return McpToolResult.Error("Give a name to look for.");
        if (await CSharpClientAsync() is not { SupportsWorkspaceSymbols: true } client)
            return McpToolResult.Error(NoCSharpServer());
        var limit = (int)Math.Clamp(McpArguments.Number(arguments, "limit") ?? 30, 1, 200);

        var symbols = (await client.WorkspaceSymbolsAsync(query, ct))
            .OrderBy(s => LspHelpers.IsSourceGenerated(s.Uri))                  // written code first
            .ThenBy(s => !string.Equals(s.Name, query, StringComparison.Ordinal))  // then exact names
            .ToList();
        if (symbols.Count == 0)
            return McpToolResult.Text($"Nothing called \"{query}\" in the solution.");

        var text = new StringBuilder();
        foreach (var symbol in symbols.Take(limit))
        {
            var container = ContainerOf(symbol.ContainerName);
            text.AppendLine($"{KindName(symbol.Kind)} {symbol.Name}{container} {Where(symbol)}");
        }
        if (symbols.Count > limit)
            text.AppendLine($"...and {symbols.Count - limit} more.");
        return McpToolResult.Text(text.ToString().TrimEnd());
    }

    // ------------------------------------------------------------ inspect_symbol

    private McpTool InspectSymbol()
    {
        var schema = McpArguments.Schema(PointArguments);
        schema["required"] = new JsonArray("path", "line", "symbol");
        return new McpTool("inspect_symbol",
            "What a name in the code is: its type or signature and documentation (the editor's hover), and where it's " +
            "defined, with that line. In C# this resolves var, overloads, extension methods and generated members. In " +
            ".axaml, on a binding path it gives the bound view-model property (and so the DataContext's type), on an " +
            "element or prefixed type its C# class.",
            schema,
            (arguments, ct) => OnUiThread(() => InspectSymbolAsync(arguments, ct)));
    }

    private async Task<McpToolResult> InspectSymbolAsync(JsonElement arguments, CancellationToken ct)
    {
        var (point, error) = await PointAsync(arguments, ct);
        if (point is null)
            return McpToolResult.Error(error!);

        return point.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
            ? await InspectXamlAsync(point, ct)
            : await InspectCSharpAsync(point, ct);
    }

    private async Task<McpToolResult> InspectCSharpAsync(CodePoint point, CancellationToken ct)
    {
        if (!point.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return McpToolResult.Error("inspect_symbol reads .cs and .axaml files.");
        if (await CSharpClientAsync() is not { } client)
            return McpToolResult.Error(NoCSharpServer());

        var text = new StringBuilder();
        var hover = await client.HoverAsync(point.Uri, point.Line, point.Character, ct);
        text.AppendLine(hover is { Text.Length: > 0 } ? HoverText.Plain(hover.Text) : "(no hover information: not a symbol, or the server is still loading)");
        if (client.SupportsDefinition)
            await AppendDefinitionsAsync(text, client, await client.DefinitionAsync(point.Uri, point.Line, point.Character, ct), ct);
        return McpToolResult.Text(text.ToString().TrimEnd());
    }

    private async Task<McpToolResult> InspectXamlAsync(CodePoint point, CancellationToken ct)
    {
        var text = new StringBuilder();
        var hover = await _xaml.GetHoverAsync(point.Path, point.Text, point.Offset);

        if (hover?.Binding is { } binding)
        {
            text.AppendLine($"Binding path ends at {binding.OwnerFullName}.{binding.Property}" +
                            (binding.PropertyType is { } type ? $" ({type})" : "") + ".");
            if (await BindingSymbols.HoverAsync(_languages, binding, ct) is { Text.Length: > 0 } csharp)
                text.AppendLine(HoverText.Plain(csharp.Text));
            else if (hover.Text.Length > 0)
                text.AppendLine(HoverText.Plain(hover.Text));
            if (await BindingSymbols.FindAsync(_languages, binding) is var (client, declarations))
                await AppendDefinitionsAsync(text, client, await CommandMethodAsync(client, binding, declarations, ct) ?? declarations, ct);
            else
                text.AppendLine("Its C# isn't in the workspace (a library's, or not built yet).");
            return McpToolResult.Text(text.ToString().TrimEnd());
        }

        // A type named in an attribute (x:DataType="vm:MainViewModel") is what's asked about, not the element it's on.
        var typeReference = XamlTypeReference.At(point.Text, point.Offset);
        if (hover is { Text.Length: > 0 } && typeReference is not { InTag: false })
            text.AppendLine(HoverText.Plain(hover.Text));
        if (typeReference is not null)
        {
            text.AppendLine($"Type {typeReference.FullName}.");
            var (client, declarations) = await XamlTypeSymbols.FindAsync(_languages, typeReference, _host.GetWorkspaceFilesAsync);
            if (declarations.Count > 0)
                await AppendDefinitionsAsync(text, client, declarations, ct);
            else
                text.AppendLine($"{typeReference.FullName} isn't defined in the workspace (a library type).");
        }
        return text.Length == 0
            ? McpToolResult.Text("Nothing known about that point (not a binding, element, attribute or type).")
            : McpToolResult.Text(text.ToString().TrimEnd());
    }

    /// <summary>
    /// For a [RelayCommand]'s generated SaveCommand, the method it was generated from (Save or SaveAsync), which is
    /// what to read or change; null when the declarations aren't all generated or there's no such method.
    /// </summary>
    private static async Task<IReadOnlyList<LspLocation>?> CommandMethodAsync(LspClient client,
        XamlCompletionService.BindingTarget binding, IReadOnlyList<LspLocation> declarations, CancellationToken ct)
    {
        if (!binding.Property.EndsWith("Command", StringComparison.Ordinal) || binding.Property.Length == "Command".Length
            || !declarations.All(d => LspHelpers.IsSourceGenerated(d.Uri)))
            return null;
        var method = binding.Property[..^"Command".Length];
        var found = (await client.WorkspaceSymbolsAsync(method, ct))
            .Where(s => (s.Name == method || s.Name == method + "Async") && s.Kind == 6 && !LspHelpers.IsSourceGenerated(s.Uri)
                        && ContainerOf(s.ContainerName) == " in " + binding.Owner)
            .ToList();
        return found.Count > 0 ? found : null;
    }

    private async Task AppendDefinitionsAsync(StringBuilder text, LspClient? client, IReadOnlyList<LspLocation> locations, CancellationToken ct)
    {
        if (locations.Count == 0)
            return;
        text.AppendLine(locations.Count == 1 ? "Defined at:" : "Defined at (partial, or several):");
        var lines = new Dictionary<string, string[]?>();
        foreach (var location in locations.Take(10))
            text.AppendLine(await DescribeLocationAsync(location, client, lines, ct));
    }

    // ------------------------------------------------------------ find_references

    private McpTool FindReferences()
    {
        var schema = McpArguments.Schema([
            .. PointArguments,
            ("includeDeclaration", "boolean", "Include the declaration itself. Default false.", null)
        ]);
        schema["required"] = new JsonArray("path", "line", "symbol");
        return new McpTool("find_references",
            "Every use of a C# symbol across the solution (the language server's Find All References), grouped by " +
            "file, each with its line of code. Exact where grep isn't: overloads, same-named members of other types, " +
            "and uses through generated code. XAML that uses it isn't included (bindings are checked at build).",
            schema,
            (arguments, ct) => OnUiThread(() => FindReferencesAsync(arguments, ct)));
    }

    private async Task<McpToolResult> FindReferencesAsync(JsonElement arguments, CancellationToken ct)
    {
        var (point, error) = await PointAsync(arguments, ct);
        if (point is null)
            return McpToolResult.Error(error!);
        if (!point.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return McpToolResult.Error("find_references works from a .cs file (for a view-model property, point at its C# declaration).");
        if (await CSharpClientAsync() is not { SupportsReferences: true } client)
            return McpToolResult.Error(NoCSharpServer());

        var includeDeclaration = arguments.TryGetProperty("includeDeclaration", out var include) && include.ValueKind == JsonValueKind.True;
        var references = await client.ReferencesAsync(point.Uri, point.Line, point.Character, ct, includeDeclaration);
        if (references.Count == 0)
            return McpToolResult.Text("No references found.");

        var text = new StringBuilder($"{references.Count} reference{(references.Count == 1 ? "" : "s")}:").AppendLine();
        var lines = new Dictionary<string, string[]?>();
        foreach (var group in references.Take(MaxLocations).GroupBy(r => r.Uri))
        {
            text.AppendLine(LspHelpers.IsSourceGenerated(group.Key)
                ? LspHelpers.SourceGeneratedName(group.Key) + " (generated)"
                : Relative(FileUris.ToPath(group.Key) ?? group.Key));
            foreach (var reference in group.OrderBy(r => r.Range.StartLine))
            {
                var code = await LineOfAsync(reference.Uri, reference.Range.StartLine, client, lines, ct);
                text.AppendLine($"  {reference.Range.StartLine + 1}: {code}");
            }
        }
        if (references.Count > MaxLocations)
            text.AppendLine($"...and {references.Count - MaxLocations} more.");
        return McpToolResult.Text(text.ToString().TrimEnd());
    }

    // ------------------------------------------------------------ helpers

    /// <summary>The point the arguments name (path, line, symbol, column), or why there isn't one.</summary>
    private async Task<(CodePoint? Point, string? Error)> PointAsync(JsonElement arguments, CancellationToken ct)
    {
        if (ResolvePath(McpArguments.String(arguments, "path")) is not { } path)
            return (null, "Give the file's path.");
        if (await TextOfAsync(path, ct) is not { } text)
            return (null, $"{Relative(path)} doesn't exist.");
        if (McpArguments.Number(arguments, "line") is not { } lineNumber)
            return (null, "Give the line the name is on.");

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var line = (int)lineNumber - 1;
        if (line < 0 || line >= lines.Length)
            return (null, $"{Relative(path)} has {lines.Length} lines.");

        var column = McpArguments.Number(arguments, "column") is { } c ? (int)c : (int?)null;
        int character;
        if (McpArguments.String(arguments, "symbol") is { Length: > 0 } symbol)
        {
            if (SymbolColumn.Find(lines[line], symbol, column) is not { } found)
                return (null, $"\"{symbol}\" isn't on line {line + 1} of {Relative(path)}: {lines[line].Trim()}");
            character = found;
        }
        else if (column is { } given)
            character = Math.Clamp(given - 1, 0, lines[line].Length);
        else
            return (null, "Give the symbol (the name as written on that line).");

        return (new CodePoint(path, text, line, character), null);
    }

    /// <summary>The offset of a 0-based line and character in text with any line endings.</summary>
    private static int OffsetOf(string text, int line, int character)
    {
        var offset = 0;
        for (var current = 0; current < line && offset < text.Length; current++)
        {
            var end = text.IndexOf('\n', offset);
            if (end < 0)
                return text.Length;
            offset = end + 1;
        }
        return Math.Min(offset + character, text.Length);
    }

    /// <summary>"path:line  code" for a definition.</summary>
    private async Task<string> DescribeLocationAsync(LspLocation location, LspClient? client, Dictionary<string, string[]?> lines, CancellationToken ct)
    {
        var code = await LineOfAsync(location.Uri, location.Range.StartLine, client, lines, ct);
        return $"  {Where(location)}  {code}";
    }

    private string Where(LspLocation location)
        => LspHelpers.IsSourceGenerated(location.Uri)
            ? $"{LspHelpers.SourceGeneratedName(location.Uri)}:{location.Range.StartLine + 1} (generated)"
            : $"{Relative(FileUris.ToPath(location.Uri) ?? location.Uri)}:{location.Range.StartLine + 1}";

    /// <summary>A 0-based line of a file (or generated document), trimmed; each file read once per call.</summary>
    private async Task<string> LineOfAsync(string uri, int line, LspClient? client, Dictionary<string, string[]?> cache, CancellationToken ct)
    {
        if (!cache.TryGetValue(uri, out var lines))
        {
            string? text;
            if (LspHelpers.IsSourceGenerated(uri))
                text = client is null ? null : await client.SourceGeneratedTextAsync(uri, ct);
            else
                text = FileUris.ToPath(uri) is { } path ? await TextOfAsync(path, ct) : null;
            cache[uri] = lines = text?.ReplaceLineEndings("\n").Split('\n');
        }
        if (lines is null || line < 0 || line >= lines.Length)
            return "";
        var code = lines[line].Trim();
        return code.Length <= 200 ? code : code[..199] + "…";
    }

    /// <summary>
    /// Roslyn's container, shortened: a member's is "in Type (project P (net10.0))", given as " in Type"; a type's is
    /// its file and project, which the path already says, so it's left out.
    /// </summary>
    private static string ContainerOf(string? container)
    {
        if (container is null || !container.StartsWith("in ", StringComparison.Ordinal))
            return "";
        var project = container.IndexOf(" (project ", StringComparison.Ordinal);
        return " " + (project > 0 ? container[..project] : container);
    }

    private string NoCSharpServer()
        => _servers.Summary.BusyText is { } busy
            ? $"The C# language server is busy ({busy}); try again in a moment."
            : "The C# language server isn't running (is a workspace with a C# project open?).";

    /// <summary>LSP's SymbolKind as a word.</summary>
    private static string KindName(int kind) => kind switch
    {
        1 => "file", 2 => "module", 3 => "namespace", 4 => "package", 5 => "class", 6 => "method", 7 => "property",
        8 => "field", 9 => "constructor", 10 => "enum", 11 => "interface", 12 => "function", 13 => "variable",
        14 => "constant", 15 => "string", 16 => "number", 17 => "boolean", 18 => "array", 19 => "object", 20 => "key",
        21 => "null", 22 => "enum-member", 23 => "struct", 24 => "event", 25 => "operator", 26 => "type-parameter",
        _ => "symbol"
    };
}
