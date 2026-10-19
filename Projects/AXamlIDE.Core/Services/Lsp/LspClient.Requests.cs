using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Lsp;

/// <summary>Feature requests to the server (diagnostics, hover, completion, navigation, symbols, rename, code actions, formatting, commands).</summary>
public sealed partial class LspClient
{
    /// <summary>
    /// Pull the document's diagnostics (textDocument/diagnostic). One request per registered
    /// identifier, merged. Always asks for a full report: we never send a previousResultId.
    /// </summary>
    public async Task<IReadOnlyList<LspDiagnostic>> PullDiagnosticsAsync(string uri, CancellationToken ct = default)
    {
        string?[] identifiers;
        lock (_diagnosticIdentifiers)
            identifiers = _diagnosticIdentifiers.Count > 0 ? [.. _diagnosticIdentifiers] : [null];

        var reports = await Task.WhenAll(identifiers.Select(id => PullOneAsync(uri, id, ct)));
        return [.. reports.SelectMany(r => r)];
    }

    private async Task<IReadOnlyList<LspDiagnostic>> PullOneAsync(string uri, string? identifier, CancellationToken ct)
    {
        var result = await RequestAsync("textDocument/diagnostic", new
        {
            textDocument = new { uri },
            identifier
        }, ct);

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return [];

        return [.. items.EnumerateArray().Select(ParseDiagnostic)];
    }

    // ------------------------------------------------------- hover / completion

    public async Task<LspHover?> HoverAsync(string uri, int line, int character, CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/hover", new
        {
            textDocument = new { uri },
            position = new { line, character }
        }, ct);

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("contents", out var contents))
            return null;

        var (content, isMarkdown) = ReadHoverMarkup(contents);
        content = content.Trim();

        if (content.Length == 0)
            return null;

        LspRange? range = result.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.Object
            ? ParseRange(r)
            : null;

        return new LspHover(content, range, isMarkdown);
    }

    public async Task<IReadOnlyList<LspCompletionItem>> CompletionAsync(
        string uri, int line, int character, string? triggerCharacter, CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/completion", new
        {
            textDocument = new { uri },
            position = new { line, character },
            // 1 = invoked, 2 = trigger character
            context = new
            {
                triggerKind = triggerCharacter is null ? 1 : 2,
                triggerCharacter
            }
        }, ct);

        // Either CompletionItem[] or { isIncomplete, items }.
        JsonElement items = result.ValueKind switch
        {
            JsonValueKind.Array => result,
            JsonValueKind.Object when result.TryGetProperty("items", out var it) => it,
            _ => default
        };

        if (items.ValueKind != JsonValueKind.Array) return [];

        return [.. items.EnumerateArray().Select(ParseCompletionItem)];
    }

    public async Task<LspSignatureHelp?> SignatureHelpAsync(
        string uri,
        int line,
        int character,
        string? triggerCharacter,
        bool isRetrigger = false,
        CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/signatureHelp", new
        {
            textDocument = new { uri },
            position = new { line, character },
            context = new
            {
                triggerKind = isRetrigger ? 3 : triggerCharacter is null ? 1 : 2,
                triggerCharacter,
                isRetrigger
            }
        }, ct);

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("signatures", out var signatures)
            || signatures.ValueKind != JsonValueKind.Array)
            return null;

        var items = signatures.EnumerateArray().Select(ParseSignatureInformation).ToArray();
        if (items.Length == 0)
            return null;

        var activeSignature = result.TryGetProperty("activeSignature", out var signatureIndex)
                              && signatureIndex.ValueKind == JsonValueKind.Number
            ? Math.Clamp(signatureIndex.GetInt32(), 0, items.Length - 1)
            : 0;
        int? activeParameter = result.TryGetProperty("activeParameter", out var parameterIndex)
                               && parameterIndex.ValueKind == JsonValueKind.Number
            ? parameterIndex.GetInt32()
            : null;

        return new LspSignatureHelp(items, activeSignature, activeParameter);
    }

    public async Task<IReadOnlyList<LspLocation>> DefinitionAsync(
        string uri, int line, int character, CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/definition", new
        {
            textDocument = new { uri },
            position = new { line, character }
        }, ct);
        return ParseLocations(result);
    }

    /// <param name="includeDeclaration">False for usages only (the code lens list), as Visual Studio shows them.</param>
    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        string uri, int line, int character, CancellationToken ct = default, bool includeDeclaration = true)
    {
        var result = await RequestAsync("textDocument/references", new
        {
            textDocument = new { uri },
            position = new { line, character },
            context = new { includeDeclaration }
        }, ct);
        return ParseLocations(result);
    }

    /// <summary>The document's code lenses (often unresolved: no title yet; see <see cref="ResolveCodeLensAsync"/>).</summary>
    public async Task<IReadOnlyList<LspCodeLens>> CodeLensAsync(string uri, CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/codeLens", new { textDocument = new { uri } }, ct);
        return result.ValueKind == JsonValueKind.Array
            ? [.. result.EnumerateArray().Where(lens => lens.TryGetProperty("range", out _)).Select(ParseCodeLens)]
            : [];
    }

    /// <summary>Fill in a lens's title ("3 references"); the lens unchanged if the server has nothing to add.</summary>
    public async Task<LspCodeLens> ResolveCodeLensAsync(LspCodeLens lens, CancellationToken ct = default)
    {
        if (lens.Title is not null)
            return lens;
        var result = await RequestAsync("codeLens/resolve", lens.Raw, ct);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("range", out _) ? ParseCodeLens(result) : lens;
    }

    private static LspCodeLens ParseCodeLens(JsonElement lens)
        => new(
            ParseRange(lens.GetProperty("range")),
            lens.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.Object ? Str(command, "title") : null,
            lens.Clone());

    /// <summary>
    /// The text of a source-generated document (a roslyn-source-generated:// URI, see
    /// <see cref="LspHelpers.IsSourceGenerated"/>): LSP 3.18's workspace/textDocumentContent, which Roslyn answers
    /// (older Roslyn builds had their own sourceGeneratedDocument/_roslyn_getText). Null if unknown.
    /// </summary>
    public async Task<string?> SourceGeneratedTextAsync(string uri, CancellationToken ct = default)
    {
        JsonElement result;
        try
        {
            result = await RequestAsync("workspace/textDocumentContent", new { uri }, ct);
        }
        catch (LspException)
        {
            result = await RequestAsync("sourceGeneratedDocument/_roslyn_getText", new { textDocument = new { uri } }, ct);
        }
        return result.ValueKind == JsonValueKind.Object ? Str(result, "text") : null;
    }

    public async Task<IReadOnlyList<LspLocation>> WorkspaceSymbolsAsync(
        string query, CancellationToken ct = default)
    {
        var result = await RequestAsync("workspace/symbol", new { query }, ct);
        if (result.ValueKind != JsonValueKind.Array)
            return [];

        return [.. result.EnumerateArray().Select(symbol =>
              {
                  var location = symbol.TryGetProperty("location", out var loc) ? loc : default;
                  return new LspLocation(
                      location.ValueKind == JsonValueKind.Object ? Str(location, "uri") ?? "" : "",
                      location.ValueKind == JsonValueKind.Object && location.TryGetProperty("range", out var range)
                          ? ParseRange(range)
                          : new LspRange(0, 0, 0, 0),
                      Str(symbol, "name"),
                      Str(symbol, "containerName"));
              }).Where(location => location.Uri.Length > 0)];
    }

    public async Task<IReadOnlyList<LspDocumentSymbol>> DocumentSymbolsAsync(
        string uri, CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/documentSymbol", new
        {
            textDocument = new { uri }
        }, ct);
        if (result.ValueKind != JsonValueKind.Array)
            return [];

        var symbols = new List<LspDocumentSymbol>();
        foreach (var symbol in result.EnumerateArray())
            ParseDocumentSymbol(symbol, null, symbols);
        return symbols;
    }

    private static void ParseDocumentSymbol(
        JsonElement symbol,
        string? containerName,
        ICollection<LspDocumentSymbol> symbols)
    {
        if (symbol.ValueKind != JsonValueKind.Object)
            return;

        var name = Str(symbol, "name");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var kind = symbol.TryGetProperty("kind", out var kindElement)
                   && kindElement.ValueKind == JsonValueKind.Number
            ? kindElement.GetInt32()
            : 0;
        LspRange? range = null;
        LspRange? selectionRange = null;
        if (symbol.TryGetProperty("range", out var rangeElement)
            && rangeElement.ValueKind == JsonValueKind.Object)
            range = ParseRange(rangeElement);
        if (symbol.TryGetProperty("selectionRange", out var selectionElement)
            && selectionElement.ValueKind == JsonValueKind.Object)
            selectionRange = ParseRange(selectionElement);

        if (range is null
            && symbol.TryGetProperty("location", out var location)
            && location.ValueKind == JsonValueKind.Object
            && location.TryGetProperty("range", out var symbolInformationRange))
        {
            range = ParseRange(symbolInformationRange);
            selectionRange = range;
        }

        if (range is { } symbolRange)
        {
            symbols.Add(new LspDocumentSymbol(
                name,
                kind,
                symbolRange,
                selectionRange ?? symbolRange,
                Str(symbol, "containerName") ?? containerName));
        }

        var childContainerName = containerName is null ? name : $"{containerName}.{name}";
        if (!symbol.TryGetProperty("children", out var children)
            || children.ValueKind != JsonValueKind.Array) return;
        foreach (var child in children.EnumerateArray())
            ParseDocumentSymbol(child, childContainerName, symbols);
    }

    public Task<JsonElement> RenameAsync(
        string uri, int line, int character, string newName, CancellationToken ct = default)
        => RequestAsync("textDocument/rename", new
        {
            textDocument = new { uri },
            position = new { line, character },
            newName
        }, ct);

    public async Task<IReadOnlyList<LspCodeAction>> CodeActionsAsync(
        string uri,
        LspRange range,
        IReadOnlyList<LspDiagnostic> diagnostics,
        CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/codeAction", new
        {
            textDocument = new { uri },
            range = ToLspRange(range),
            context = new
            {
                diagnostics = diagnostics.Select(diagnostic => diagnostic.Raw
                    ?? JsonSerializer.SerializeToElement(new
                    {
                        range = ToLspRange(new LspRange(
                            diagnostic.StartLine, diagnostic.StartChar,
                            diagnostic.EndLine, diagnostic.EndChar)),
                        severity = diagnostic.Severity,
                        message = diagnostic.Message
                    })).ToArray()
            }
        }, ct);

        if (result.ValueKind != JsonValueKind.Array)
            return [];

        return [.. result.EnumerateArray().Select(ParseCodeAction)
                  .Where(action => action is not null).Select(action => action!)];
    }

    public async Task<LspCodeAction> ResolveCodeActionAsync(LspCodeAction action, CancellationToken ct = default)
    {
        var result = await RequestAsync("codeAction/resolve", action.Raw, ct);
        return ParseCodeAction(result)
               ?? throw new LspException($"The language server returned an invalid resolved action for '{action.Title}'.");
    }

    public async Task<IReadOnlyList<LspTextEdit>> FormattingAsync(
        string uri,
        int tabSize,
        bool insertSpaces,
        CancellationToken ct = default)
    {
        var result = await RequestAsync("textDocument/formatting", new
        {
            textDocument = new { uri },
            options = new { tabSize, insertSpaces }
        }, ct);

        if (result.ValueKind != JsonValueKind.Array)
            return [];

        return [.. result.EnumerateArray()
                  .Where(edit => edit.ValueKind == JsonValueKind.Object
                                 && edit.TryGetProperty("range", out _)
                                 && edit.TryGetProperty("newText", out _))
                  .Select(edit => new LspTextEdit(
                      ParseRange(edit.GetProperty("range")),
                      Str(edit, "newText") ?? ""))];
    }

    public Task<JsonElement> ExecuteCommandAsync(
        string command,
        JsonElement? arguments,
        CancellationToken ct = default)
        => RequestAsync("workspace/executeCommand", new
        {
            command,
            arguments = arguments is { ValueKind: JsonValueKind.Array } values
                ? values
                : JsonEmptyArray
        }, ct);


    public Task<JsonElement> ResolveFixAllAsync(
        JsonElement data,
        string scope,
        CancellationToken ct = default)
        => RequestAsync("codeAction/resolveFixAll", new
        {
            title = data.GetProperty("UniqueIdentifier").GetString(),
            data,
            scope
        }, ct);
}
