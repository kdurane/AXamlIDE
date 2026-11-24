using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Lsp;

/// <summary>
/// Roslyn's extension messages: load an assembly of ours into the server and call its handlers, which run on the
/// server's own live Solution (see AXamlIDE.RoslynExtension, and <see cref="RoslynExtensionClient"/> for ours).
/// </summary>
public sealed partial class LspClient
{
    /// <summary>
    /// Load an extension assembly into the server (server/_vs_activateExtension). Returns the message names of its
    /// workspace and document handlers; throws if the server can't load it, or doesn't know the request.
    /// </summary>
    public async Task<IReadOnlyList<string>> ActivateExtensionAsync(string assemblyPath, CancellationToken ct)
    {
        var result = await RequestAsync("server/_vs_activateExtension", new { assemblyFilePath = assemblyPath }, ct);
        if (result.ValueKind != JsonValueKind.Object)
            return [];
        if (result.TryGetProperty("extensionException", out var exception) && exception.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new InvalidOperationException($"The language server couldn't load {assemblyPath}: {exception.GetRawText()}");

        static IEnumerable<string> Names(JsonElement result, string property)
            => result.TryGetProperty(property, out var names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String).Select(name => name.GetString()!)
                : [];
        return [.. Names(result, "workspaceMessageHandlers"), .. Names(result, "documentMessageHandlers")];
    }

    /// <summary>
    /// Call a workspace handler of a loaded extension (workspace/_vs_dispatchExtensionMessage): the message goes as
    /// JSON text, and the handler's answer comes back as JSON text. Throws if the handler failed or the extension was
    /// unloaded.
    /// </summary>
    public async Task<string> DispatchExtensionMessageAsync(string messageName, string messageJson, CancellationToken ct)
    {
        var result = await RequestAsync("workspace/_vs_dispatchExtensionMessage", new { messageName, message = messageJson }, ct);
        if (result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"No answer to {messageName}.");
        if (result.TryGetProperty("extensionWasUnloaded", out var unloaded) && unloaded.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException($"The extension handling {messageName} was unloaded.");
        if (result.TryGetProperty("extensionException", out var exception) && exception.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new InvalidOperationException($"{messageName} failed: {exception.GetRawText()}");
        return result.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.String
            ? response.GetString()!
            : throw new InvalidOperationException($"No answer to {messageName}.");
    }
}
