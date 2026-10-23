using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Lsp;

/// <summary>Messages from the server: responses, server-to-client requests, capability (un)registration, notifications, progress and published diagnostics.</summary>
public sealed partial class LspClient
{
    private void Dispatch(JsonElement root)
    {
        var hasId = root.TryGetProperty("id", out var id);
        var hasMethod = root.TryGetProperty("method", out var method);

        if (hasMethod && hasId) _ = HandleServerRequestAsync(id.Clone(), method.GetString()!, root);
        else if (hasMethod) HandleNotification(method.GetString()!, root);
        else if (hasId) HandleResponse(id, root);
    }

    private void HandleResponse(JsonElement idEl, JsonElement root)
    {
        if (!idEl.TryGetInt32(out var id) || !_pending.TryRemove(id, out var tcs)) return;

        if (root.TryGetProperty("error", out var err))
        {
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
            tcs.TrySetException(new LspException(msg ?? "unknown error"));
        }
        else
        {
            // Clone: the JsonDocument is disposed as soon as Dispatch returns.
            tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : JsonNull);
        }
    }

    // Servers call back too (workspace/configuration, client/registerCapability,
    // window/workDoneProgress/create ...). Every request needs *a* response.
    private async Task HandleServerRequestAsync(JsonElement id, string method, JsonElement root)
    {
        object result = JsonNull;
        root.TryGetProperty("params", out var p);   // Undefined when the request carries none

        switch (method)
        {
            case "workspace/configuration" when p.ValueKind == JsonValueKind.Object
                                                && p.TryGetProperty("items", out var items)
                                                && items.ValueKind == JsonValueKind.Array:
                // One answer per item, in order; null leaves the server's default.
                result = items.EnumerateArray()
                    .Select(item => (item.ValueKind == JsonValueKind.Object && Str(item, "section") is { } section
                        ? _settings?.Invoke(section)
                        : null) ?? JsonNull)
                    .ToArray();
                break;

            case "client/registerCapability":
                RegisterCapabilities(p);
                break;

            case "client/unregisterCapability":
                UnregisterCapabilities(p);
                break;

            case "workspace/diagnostic/refresh":
                DiagnosticRefreshRequested?.Invoke();
                break;

            case "workspace/codeLens/refresh":
                CodeLensRefreshRequested?.Invoke();
                break;

            case "workspace/semanticTokens/refresh":
                SemanticTokensRefreshRequested?.Invoke();
                break;

            case "workspace/inlayHint/refresh":
                InlayHintsRefreshRequested?.Invoke();
                break;

            case "workspace/applyEdit":
                var applied = false;
                if (WorkspaceEditHandler is { } handler
                    && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("edit", out var edit))
                {
                    try
                    {
                        applied = await handler(edit.Clone());
                    }
                    catch (Exception ex)
                    {
                        Log($"Couldn't apply language-server workspace edit: {ex.Message}");
                    }
                }
                result = new { applied };
                break;
        }

        Enqueue(new { jsonrpc = "2.0", id, result });
        if (method == "workspace/configuration")
            ConfigurationAnswered?.Invoke();
    }

    private void RegisterCapabilities(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty("registrations", out var registrations)
            || registrations.ValueKind != JsonValueKind.Array)
            return;

        var anyDiagnostics = false;

        foreach (var registration in registrations.EnumerateArray())
        {
            var method = Str(registration, "method");
            if (method is "textDocument/definition" or "textDocument/references"
                or "textDocument/rename" or "workspace/symbol" or "textDocument/codeAction"
                or "textDocument/formatting" or "textDocument/documentSymbol" or "textDocument/codeLens")
            {
                _dynamicCapabilities.Add(method);
                SupportsDefinition |= method == "textDocument/definition";
                SupportsReferences |= method == "textDocument/references";
                SupportsRename |= method == "textDocument/rename";
                SupportsWorkspaceSymbols |= method == "workspace/symbol";
                SupportsDocumentSymbols |= method == "textDocument/documentSymbol";
                SupportsCodeActions |= method == "textDocument/codeAction";
                SupportsDocumentFormatting |= method == "textDocument/formatting";
                SupportsCodeLens |= method == "textDocument/codeLens";
                if (method == "textDocument/codeAction"
                    && registration.TryGetProperty("registerOptions", out var actionOptions)
                    && actionOptions.ValueKind == JsonValueKind.Object)
                    _dynamicCodeActionResolve = actionOptions.TryGetProperty("resolveProvider", out var resolveProvider)
                                                && resolveProvider.ValueKind == JsonValueKind.True;
            }

            if (method != "textDocument/diagnostic")
                continue;

            anyDiagnostics = true;
            SupportsPullDiagnostics = true;

            if (registration.TryGetProperty("registerOptions", out var options)
                && options.ValueKind == JsonValueKind.Object)
                AddDiagnosticIdentifier(Str(options, "identifier"));
        }

        // Documents attached before this arrived have not been pulled yet.
        if (anyDiagnostics) DiagnosticRefreshRequested?.Invoke();
    }

    private void UnregisterCapabilities(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object
            || (!p.TryGetProperty("unregisterations", out var registrations)
                && !p.TryGetProperty("unregistrations", out registrations))
            || registrations.ValueKind != JsonValueKind.Array)
            return;

        var removedCodeActions = false;
        foreach (var registration in registrations.EnumerateArray())
            if (Str(registration, "method") is { } method)
            {
                _dynamicCapabilities.Remove(method);
                removedCodeActions |= method == "textDocument/codeAction";
            }

        if (removedCodeActions)
            _dynamicCodeActionResolve = false;

        SupportsDefinition = _staticDefinition || _dynamicCapabilities.Contains("textDocument/definition");
        SupportsReferences = _staticReferences || _dynamicCapabilities.Contains("textDocument/references");
        SupportsRename = _staticRename || _dynamicCapabilities.Contains("textDocument/rename");
        SupportsWorkspaceSymbols = _staticWorkspaceSymbols || _dynamicCapabilities.Contains("workspace/symbol");
        SupportsDocumentSymbols = _staticDocumentSymbols || _dynamicCapabilities.Contains("textDocument/documentSymbol");
        SupportsCodeActions = _staticCodeActions || _dynamicCapabilities.Contains("textDocument/codeAction");
        SupportsCodeActionResolve = _staticCodeActionResolve || _dynamicCodeActionResolve;
        SupportsDocumentFormatting = _staticDocumentFormatting
                                     || _dynamicCapabilities.Contains("textDocument/formatting");
        SupportsCodeLens = _staticCodeLens || _dynamicCapabilities.Contains("textDocument/codeLens");
    }

    private void AddDiagnosticIdentifier(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return;

        lock (_diagnosticIdentifiers)
            if (!_diagnosticIdentifiers.Contains(identifier))
                _diagnosticIdentifiers.Add(identifier);
    }

    private void HandleNotification(string method, JsonElement root)
    {
        root.TryGetProperty("params", out var p);   // some notifications carry none

        switch (method)
        {
            case "workspace/projectInitializationComplete":
                ProjectInitializationComplete?.Invoke();
                break;

            case "$/progress" when p.ValueKind == JsonValueKind.Object:
                HandleProgress(p);
                break;

            case "textDocument/publishDiagnostics" when p.ValueKind == JsonValueKind.Object:
                HandlePublishDiagnostics(p);
                break;

            case "window/logMessage" or "window/showMessage"
                when p.ValueKind == JsonValueKind.Object && Str(p, "message") is { } message:
                Log(message);
                break;
        }
    }

    // $/progress for server-initiated work (window/workDoneProgress/create), e.g. loading projects.
    private void HandleProgress(JsonElement p)
    {
        if (!p.TryGetProperty("token", out var tokenElement)
            || !p.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Object)
            return;

        var token = tokenElement.GetRawText();   // the token may be a string or a number
        string? text;

        lock (_progress)
        {
            switch (Str(value, "kind"))
            {
                case "begin":
                    _progress[token] = (Str(value, "title") ?? "", Str(value, "message"));
                    break;
                case "report" when _progress.TryGetValue(token, out var current):
                    _progress[token] = (current.Title, Str(value, "message") ?? current.Message);
                    break;
                case "end":
                    _progress.Remove(token);
                    break;
                default:
                    return;
            }

            text = _progress.Count == 0
                ? null
                : string.Join("; ", _progress.Values.Select(v =>
                    string.IsNullOrEmpty(v.Message) ? v.Title : $"{v.Title}: {v.Message}"));
        }

        Progress?.Invoke(text);
    }

    private void HandlePublishDiagnostics(JsonElement p)
    {
        var uri = p.GetProperty("uri").GetString()!;

        int? version = p.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

        var list = p.GetProperty("diagnostics").EnumerateArray().Select(ParseDiagnostic).ToList();

        DiagnosticsPublished?.Invoke(uri, list, version);
    }

    private static LspDiagnostic ParseDiagnostic(JsonElement d)
    {
        var range = ParseRange(d.GetProperty("range"));

        var severity = d.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.Number
            ? sev.GetInt32()
            : 1;

        var message = ReadMarkup(d.GetProperty("message"));

        // Lead with the code (CS0103, IDE0005 ...) when there is one.
        if (!d.TryGetProperty("code", out var code))
            return new LspDiagnostic(range.StartLine, range.StartChar, range.EndLine, range.EndChar, severity, message, d.Clone());
        var codeText = code.ValueKind switch
        {
            JsonValueKind.String => code.GetString(),
            JsonValueKind.Number => code.GetRawText(),
            _ => null
        };
        if (!string.IsNullOrEmpty(codeText)) message = $"{codeText}: {message}";

        return new LspDiagnostic(range.StartLine, range.StartChar, range.EndLine, range.EndChar, severity, message, d.Clone());
    }
}
