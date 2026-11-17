using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AXamlIDE.Services.Mcp;

/// <summary>What a tool gives back: text and pictures, in order. <see cref="IsError"/> tells the model the call failed.</summary>
public sealed record McpToolResult(IReadOnlyList<McpContent> Content, bool IsError = false)
{
    public static McpToolResult Text(string text) => new([new McpContent.Text(text)]);
    public static McpToolResult Error(string text) => new([new McpContent.Text(text)], IsError: true);
}

public abstract record McpContent
{
    public sealed record Text(string Value) : McpContent;
    public sealed record Png(byte[] Bytes) : McpContent;
}

/// <summary>One tool: its name, what it's for (the model reads this to decide when to call it), its arguments as a JSON Schema, and the code.</summary>
public sealed record McpTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<JsonElement, CancellationToken, Task<McpToolResult>> Run);

/// <summary>
/// A Model Context Protocol server over Streamable HTTP, cut down to what a tools-only server needs: initialize, ping,
/// tools/list and tools/call, each POST answered with one JSON response (no SSE stream, no sessions). It listens on
/// localhost only, and turns away any request a web page could have sent (an Origin or Host that isn't localhost), so
/// a site open in a browser can't call the tools.
///
/// <see cref="HandleAsync"/> is the protocol on its own (tests call it with a request's body); <see cref="Start"/> puts
/// it on a port.
/// </summary>
public sealed class McpServer : IDisposable
{
    /// <summary>The versions this server understands (the client's is echoed back when it's one of these).</summary>
    private static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26"];

    private readonly string _name;
    private readonly string _version;
    private readonly string? _instructions;
    private readonly Dictionary<string, McpTool> _tools;
    private HttpListener? _listener;
    private CancellationTokenSource? _stopping;

    public McpServer(string name, string version, IEnumerable<McpTool> tools, string? instructions = null)
    {
        _name = name;
        _version = version;
        _instructions = instructions;
        _tools = tools.ToDictionary(tool => tool.Name);
    }

    /// <summary>
    /// Asked before each tool call with the client's name (the URL's ?client=, null without one): a reason to turn the
    /// call away (the model sees it), or null to run it. Lets one client's tools be switched off while another's stay on.
    /// </summary>
    public Func<string?, string?>? RefuseCall { get; set; }

    /// <summary>The address clients connect to (http://localhost:port/mcp), or null while it isn't listening.</summary>
    public string? Url { get; private set; }

    /// <summary>
    /// Listen on <paramref name="port"/>, or on any free port when that one is taken (another IDE window has it).
    /// Returns false, and stays off, if it can't listen at all.
    /// </summary>
    public bool Start(int port)
    {
        if (_listener is not null)
            return true;
        foreach (var candidate in new[] { port, 0 })
        {
            var listener = new HttpListener();
            var chosen = candidate == 0 ? FreePort() : candidate;
            listener.Prefixes.Add($"http://localhost:{chosen}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                Debug.WriteLine($"[mcp] Couldn't listen on port {chosen}: {ex.Message}");
                listener.Close();
                continue;
            }

            _listener = listener;
            _stopping = new CancellationTokenSource();
            Url = $"http://localhost:{chosen}/mcp";
            _ = AcceptLoopAsync(listener, _stopping.Token);
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        _stopping?.Cancel();
        _listener?.Close();
        _listener = null;
        Url = null;
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[mcp] {ex.Message}");
                continue;
            }
            _ = ServeAsync(context, ct);
        }
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (!IsLocal(request.Headers["Origin"]) || !IsLocal(request.Headers["Host"]))
            {
                response.StatusCode = 403;
                return;
            }
            if (request.Url?.AbsolutePath != "/mcp")
            {
                response.StatusCode = 404;
                return;
            }
            if (request.HttpMethod == "DELETE")
            {
                response.StatusCode = 200;   // the client ending its session: there's nothing to free
                return;
            }
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;   // GET would open a stream for messages from the server: this one never sends any
                response.AddHeader("Allow", "POST");
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync(ct);

            var answer = await HandleAsync(body, request.QueryString["client"], ct);
            if (answer is null)
            {
                response.StatusCode = 202;   // a notification or a response: nothing to say back
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(answer);
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mcp] Request failed: {ex}");
            try { response.StatusCode = 500; } catch (InvalidOperationException) { }   // headers already sent
        }
        finally
        {
            try { response.Close(); } catch (Exception) { }
        }
    }

    /// <summary>No header (not a browser), or one naming this machine. "localhost:47613" and "http://127.0.0.1:47613" both count.</summary>
    internal static bool IsLocal(string? header)
    {
        if (string.IsNullOrEmpty(header))
            return true;
        var host = Uri.TryCreate(header, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host
            : header.Split(':')[0];
        return host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    /// <summary>
    /// One JSON-RPC message in, its JSON-RPC response out; null for a notification (it has no id) or a response
    /// to us. Tool failures come back as a result with isError, so the model sees them; protocol mistakes as errors.
    /// </summary>
    public async Task<string?> HandleAsync(string body, string? client = null, CancellationToken ct = default)
    {
        JsonElement message;
        try
        {
            message = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            return Error(null, -32700, "The request isn't valid JSON.");
        }
        if (message.ValueKind != JsonValueKind.Object)
            return Error(null, -32600, "Send one JSON-RPC message per request.");

        var id = message.TryGetProperty("id", out var idElement) ? JsonNode.Parse(idElement.GetRawText()) : null;
        if (!message.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            return null;   // a response to a request we never send
        var method = methodElement.GetString();
        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        if (id is null)
            return null;   // notifications/initialized, notifications/cancelled...

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => RefuseCall?.Invoke(client) is { } reason
                    ? Result(McpToolResult.Error(reason))
                    : await CallToolAsync(parameters, ct),
                _ => null
            };
            return result is null
                ? Error(id, -32601, $"Unknown method '{method}'.")
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
        }
        catch (McpProtocolException ex)
        {
            return Error(id, -32602, ex.Message);
        }
    }

    private JsonObject Initialize(JsonElement parameters)
    {
        var asked = parameters.ValueKind == JsonValueKind.Object
                    && parameters.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
        var result = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = _name, ["version"] = _version }
        };
        if (_instructions is not null)
            result["instructions"] = _instructions;
        return result;
    }

    private JsonObject ListTools() => new()
    {
        ["tools"] = new JsonArray([
            .. _tools.Values.Select(tool => (JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone()
            })
        ])
    };

    private async Task<JsonObject> CallToolAsync(JsonElement parameters, CancellationToken ct)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var nameElement) || nameElement.GetString() is not { } name)
            throw new McpProtocolException("tools/call needs a tool name.");
        if (!_tools.TryGetValue(name, out var tool))
            throw new McpProtocolException($"There's no tool called '{name}'.");

        var arguments = parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : JsonDocument.Parse("{}").RootElement;
        McpToolResult result;
        try
        {
            result = await tool.Run(arguments, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mcp] {name} failed: {ex}");
            result = McpToolResult.Error($"{name} failed: {ex.Message}");
        }

        return Result(result);
    }

    private static JsonObject Result(McpToolResult result) => new()
    {
        ["content"] = new JsonArray([.. result.Content.Select(ToJson)]),
        ["isError"] = result.IsError
    };

    private static JsonNode ToJson(McpContent content) => content switch
    {
        McpContent.Text text => new JsonObject { ["type"] = "text", ["text"] = text.Value },
        McpContent.Png png => new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png.Bytes), ["mimeType"] = "image/png" },
        _ => throw new NotSupportedException(content.GetType().Name)
    };

    private static string Error(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        }.ToJsonString();

    private sealed class McpProtocolException(string message) : Exception(message);
}

/// <summary>Reading a tool's arguments: missing or the wrong type gives the default.</summary>
public static class McpArguments
{
    public static string? String(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static double? Number(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    /// <summary>A JSON Schema object with these properties, each { "type", "description" } (and "enum" when given).</summary>
    public static JsonObject Schema(params (string Name, string Type, string Description, string[]? Enum)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, type, description, values) in properties)
        {
            var property = new JsonObject { ["type"] = type, ["description"] = description };
            if (values is not null)
                property["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]);
            props[name] = property;
        }
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }
}
