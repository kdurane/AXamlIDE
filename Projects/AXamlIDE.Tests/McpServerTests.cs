using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AXamlIDE.Services.Mcp;

namespace AXamlIDE.Tests;

public sealed class McpServerTests
{
    private static McpServer Server() => new("test", "1.0",
    [
        new McpTool("echo", "Says it back.", McpArguments.Schema(("text", "string", "What to say.", null)),
            (arguments, _) => Task.FromResult(McpToolResult.Text(McpArguments.String(arguments, "text") ?? ""))),
        new McpTool("picture", "A picture.", McpArguments.Schema(),
            (_, _) => Task.FromResult(new McpToolResult([new McpContent.Png([1, 2, 3])]))),
        new McpTool("broken", "Throws.", McpArguments.Schema(),
            (_, _) => throw new InvalidOperationException("no host"))
    ], "Some instructions.");

    private static async Task<JsonElement> Call(McpServer server, string body)
        => JsonDocument.Parse((await server.HandleAsync(body))!).RootElement;

    [Fact]
    public async Task Initialize_echoes_a_known_version_and_says_it_has_tools()
    {
        var reply = await Call(Server(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}""");

        var result = reply.GetProperty("result");
        Assert.Equal(1, reply.GetProperty("id").GetInt32());
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal("Some instructions.", result.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task An_unknown_version_gets_the_newest()
    {
        var reply = await Call(Server(), """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        Assert.Equal("a", reply.GetProperty("id").GetString());
        Assert.Equal("2025-11-25", reply.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task A_notification_gets_no_answer()
        => Assert.Null(await Server().HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

    [Fact]
    public async Task Tools_are_listed_with_their_schemas()
    {
        var reply = await Call(Server(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = reply.GetProperty("result").GetProperty("tools");
        Assert.Equal(3, tools.GetArrayLength());
        Assert.Equal("echo", tools[0].GetProperty("name").GetString());
        Assert.Equal("string", tools[0].GetProperty("inputSchema").GetProperty("properties").GetProperty("text").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_call_returns_the_tools_text()
    {
        var reply = await Call(Server(), """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""");

        var result = reply.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("hi", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_picture_is_base64_png()
    {
        var reply = await Call(Server(), """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"picture"}}""");

        var content = reply.GetProperty("result").GetProperty("content")[0];
        Assert.Equal("image", content.GetProperty("type").GetString());
        Assert.Equal("image/png", content.GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), content.GetProperty("data").GetString());
    }

    [Fact]
    public async Task A_tool_that_throws_is_an_error_result_the_model_sees()
    {
        var reply = await Call(Server(), """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"broken"}}""");

        var result = reply.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("no host", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"missing"}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":6,"method":"resources/list"}""", -32601)]
    [InlineData("""not json""", -32700)]
    public async Task Protocol_mistakes_are_json_rpc_errors(string body, int code)
    {
        var reply = await Call(Server(), body);

        Assert.Equal(code, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("localhost:47613", true)]
    [InlineData("127.0.0.1:47613", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("evil.example:47613", false)]
    public void Only_this_machine_counts_as_local(string? header, bool local)
        => Assert.Equal(local, McpServer.IsLocal(header));

    [Fact]
    public async Task It_answers_over_http_and_turns_away_web_pages()
    {
        using var server = Server();
        Assert.True(server.Start(0));
        using var http = new HttpClient();

        var ok = await http.PostAsync(server.Url, new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)ok.StatusCode);
        Assert.Contains("\"result\":{}", await ok.Content.ReadAsStringAsync());

        var fromPage = new HttpRequestMessage(HttpMethod.Post, server.Url)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json")
        };
        fromPage.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(403, (int)(await http.SendAsync(fromPage)).StatusCode);

        var notification = await http.PostAsync(server.Url, new StringContent("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal(202, (int)notification.StatusCode);
    }

    [Fact]
    public async Task A_client_that_is_switched_off_has_its_calls_turned_away()
    {
        using var server = Server();
        server.RefuseCall = client => client == "panel" ? "Turned off." : null;
        const string call = """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""";

        var refused = JsonDocument.Parse((await server.HandleAsync(call, "panel"))!).RootElement.GetProperty("result");
        var allowed = JsonDocument.Parse((await server.HandleAsync(call, null))!).RootElement.GetProperty("result");

        Assert.True(refused.GetProperty("isError").GetBoolean());
        Assert.Equal("Turned off.", refused.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("hi", allowed.GetProperty("content")[0].GetProperty("text").GetString());
    }
}
