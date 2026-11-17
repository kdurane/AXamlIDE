using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AXamlIDE.Services.Agents;

namespace AXamlIDE.Tests;

/// <summary>The Agent Client Protocol parser, using messages captured from claude-agent-acp 0.107.</summary>
public sealed class AcpParserTests
{
    private static AcpUpdate? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return AcpParser.ParseUpdate(document.RootElement);
    }

    [Fact]
    public void Reads_a_message_chunk()
    {
        var update = Parse("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"I changed `hello.tx"},"messageId":"msg_1"}""");

        Assert.Equal(new AcpMessageChunk(AcpChunkKind.Agent, "I changed `hello.tx", "msg_1"), update);
    }

    [Fact]
    public void Reads_a_thought_without_a_message_id()
    {
        var update = Parse("""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"hmm"}}""");

        Assert.Equal(new AcpMessageChunk(AcpChunkKind.Thought, "hmm", null), update);
    }

    [Fact]
    public void Reads_a_new_tool_call()
    {
        var update = Assert.IsType<AcpToolCall>(Parse("""
            {"_meta":{"claudeCode":{"toolName":"Read"}},"toolCallId":"toolu_1","sessionUpdate":"tool_call","name":"Read",
             "rawInput":{},"status":"pending","title":"Read File","kind":"read","content":[],"locations":[]}
            """));

        Assert.True(update.IsNew);
        Assert.Equal("toolu_1", update.Id);
        Assert.Equal("Read File", update.Title);
        Assert.Equal("read", update.Kind);
        Assert.Equal("pending", update.Status);
        Assert.Empty(update.Content!);
    }

    [Fact]
    public void An_update_only_carries_what_changed()
    {
        var update = Assert.IsType<AcpToolCall>(Parse("""
            {"toolCallId":"toolu_1","sessionUpdate":"tool_call_update","title":"Read hello.txt",
             "locations":[{"path":"/w/hello.txt","line":1}]}
            """));

        Assert.False(update.IsNew);
        Assert.Equal("Read hello.txt", update.Title);
        Assert.Null(update.Status);
        Assert.Null(update.Content);
        Assert.Equal([new AcpLocation("/w/hello.txt", 1)], update.Locations);
    }

    [Fact]
    public void Reads_a_diff_and_text_content()
    {
        var update = Assert.IsType<AcpToolCall>(Parse("""
            {"toolCallId":"t","sessionUpdate":"tool_call_update","content":[
              {"type":"diff","path":"/w/a.txt","oldText":"hello world","newText":"dlrow olleh"},
              {"type":"diff","path":"/w/new.txt","newText":"made"},
              {"type":"content","content":{"type":"text","text":"```console\nhello.txt\n```"}},
              {"type":"terminal","terminalId":"x"}]}
            """));

        Assert.Equal<AcpToolContent>(
        [
            new AcpDiffContent("/w/a.txt", "hello world", "dlrow olleh"),
            new AcpDiffContent("/w/new.txt", null, "made"),
            new AcpTextContent("```console\nhello.txt\n```")
        ], update.Content!);
    }

    [Fact]
    public void Reads_a_plan()
    {
        var plan = Assert.IsType<AcpPlan>(Parse("""
            {"sessionUpdate":"plan","entries":[{"content":"Read","priority":"high","status":"completed"},{"content":"Edit","priority":"high","status":"in_progress"}]}
            """));

        Assert.Equal([new AcpPlanEntry("Read", "completed"), new AcpPlanEntry("Edit", "in_progress")], plan.Entries);
    }

    [Fact]
    public void Reads_the_config_options()
    {
        var update = Assert.IsType<AcpConfigOptionsChanged>(Parse("""
            {"sessionUpdate":"config_option_update","configOptions":[
              {"id":"mode","name":"Mode","category":"mode","type":"select","currentValue":"auto","options":[
                {"value":"default","name":"Manual","description":"Always ask before making changes"},
                {"value":"auto","name":"Auto","description":"Claude handles permission decisions"}]},
              {"id":"fast","name":"Fast","type":"boolean","currentValue":true}]}
            """));

        var mode = Assert.Single(update.Options);   // the boolean one isn't shown
        Assert.Equal("mode", mode.Id);
        Assert.Equal("auto", mode.CurrentValue);
        Assert.Equal(["Manual", "Auto"], mode.Choices.Select(choice => choice.Name));
    }

    [Fact]
    public void Reads_the_title_and_usage()
    {
        Assert.Equal(new AcpSessionTitle("Reverse hello.txt text"),
            Parse("""{"sessionUpdate":"session_info_update","title":"Reverse hello.txt text","updatedAt":"2026-11-16T14:22:53.018Z"}"""));
        Assert.Equal(new AcpUsage(33265, 1000000), Parse("""{"sessionUpdate":"usage_update","used":33265,"size":1000000}"""));
    }

    [Fact]
    public void Unknown_and_malformed_updates_are_skipped()
    {
        Assert.Null(Parse("""{"sessionUpdate":"available_commands_update","availableCommands":[]}"""));
        Assert.Null(Parse("""{"sessionUpdate":"tool_call"}"""));                    // no id
        Assert.Null(Parse("""{"sessionUpdate":"agent_message_chunk","content":{"type":"image","data":"..."}}"""));
        Assert.Null(Parse("[]"));
    }

    [Fact]
    public void Reads_a_permission_request()
    {
        using var document = JsonDocument.Parse("""
            {"toolCall":{"toolCallId":"toolu_2","name":"Bash","status":"pending","title":"touch made.txt","kind":"execute",
             "content":[{"type":"content","content":{"type":"text","text":"Create empty file made.txt"}}]},
             "options":[{"optionId":"allow-once","name":"Yes","kind":"allow_once"},{"optionId":"reject","name":"No","kind":"reject_once"}],
             "sessionId":"s1"}
            """);

        var request = AcpParser.PermissionRequest(document.RootElement)!;

        Assert.Equal("s1", request.SessionId);
        Assert.Equal("touch made.txt", request.ToolCall.Title);
        Assert.Equal([new AcpPermissionOption("allow-once", "Yes", "allow_once"), new AcpPermissionOption("reject", "No", "reject_once")], request.Options);
        Assert.True(request.Options[0].Allows);
        Assert.False(request.Options[1].Allows);
    }
}

/// <summary>AcpClient against a scripted agent on the other end of two pipes.</summary>
public sealed class AcpClientTests : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _toClient = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _fromClient = new(PipeDirection.In);
    private readonly StreamWriter _agentWrites;
    private readonly StreamReader _agentReads;
    private readonly AcpClient _client;

    public AcpClientTests()
    {
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, _toClient.ClientSafePipeHandle);
        var clientOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromClient.ClientSafePipeHandle);
        _client = new AcpClient(clientIn, clientOut);
        _agentWrites = new StreamWriter(_toClient, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _agentReads = new StreamReader(_fromClient, new UTF8Encoding(false));
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _toClient.Dispose();
        _fromClient.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The next message the client sent.</summary>
    private async Task<JsonObject> ReceiveAsync()
    {
        var line = await _agentReads.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return JsonNode.Parse(line!)!.AsObject();
    }

    private Task SendAsync(string json) => _agentWrites.WriteLineAsync(json.ReplaceLineEndings(" "));

    private async Task HandshakeAsync()
    {
        var initialize = _client.InitializeAsync();
        var request = await ReceiveAsync();
        Assert.Equal("initialize", (string?)request["method"]);
        Assert.Equal(1, (int?)request["params"]!["protocolVersion"]);
        Assert.False((bool?)request["params"]!["clientCapabilities"]!["terminal"]);
        Assert.True((bool?)request["params"]!["clientCapabilities"]!["_meta"]!["terminal-auth"]);   // so it lists its sign-in commands
        await SendAsync($$$"""{"jsonrpc":"2.0","id":{{{request["id"]}}},"result":{"protocolVersion":1,"agentInfo":{"name":"x","title":"Claude Agent","version":"0.107.2"},"authMethods":[]}}""");
        await initialize.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Initializes_and_reads_the_agent_name()
    {
        await HandshakeAsync();

        Assert.Equal("Claude Agent 0.107.2", _client.AgentTitle);
    }

    [Fact]
    public async Task A_newer_protocol_version_is_refused()
    {
        var initialize = _client.InitializeAsync();
        var request = await ReceiveAsync();
        await SendAsync($$$"""{"jsonrpc":"2.0","id":{{{request["id"]}}},"result":{"protocolVersion":2}}""");

        var error = await Assert.ThrowsAsync<AcpException>(() => initialize.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("version 2", error.Message);
    }

    [Fact]
    public async Task Runs_a_prompt_turn_and_reports_its_updates()
    {
        await HandshakeAsync();
        var updates = new ConcurrentQueue<(string, AcpUpdate)>();
        _client.Updated += (session, update) => updates.Enqueue((session, update));

        var newSession = _client.NewSessionAsync("/work");
        var request = await ReceiveAsync();
        Assert.Equal("session/new", (string?)request["method"]);
        Assert.Equal("/work", (string?)request["params"]!["cwd"]);
        await SendAsync($$$"""
            {"jsonrpc":"2.0","id":{{{request["id"]}}},"result":{"sessionId":"s1","configOptions":[
              {"id":"model","name":"Model","type":"select","currentValue":"default","options":[{"value":"default","name":"Default"}]}]}}
            """);
        var session = await newSession.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("s1", session.Id);
        Assert.Equal("model", Assert.Single(session.Options).Id);

        var prompt = _client.PromptAsync("s1", [new AcpPromptBlock.Text("hi"), new AcpPromptBlock.File("/work/a b.cs"), new AcpPromptBlock.Image([1, 2, 3], "image/png")]);
        request = await ReceiveAsync();
        Assert.Equal("session/prompt", (string?)request["method"]);
        var blocks = request["params"]!["prompt"]!.AsArray();
        Assert.Equal("hi", (string?)blocks[0]!["text"]);
        Assert.Equal("resource_link", (string?)blocks[1]!["type"]);
        Assert.Equal("file:///work/a%20b.cs", (string?)blocks[1]!["uri"]);
        Assert.Equal("a b.cs", (string?)blocks[1]!["name"]);
        Assert.Equal("image", (string?)blocks[2]!["type"]);
        Assert.Equal("AQID", (string?)blocks[2]!["data"]);
        Assert.Equal("image/png", (string?)blocks[2]!["mimeType"]);

        await SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello"}}}}""");
        await SendAsync($$$"""{"jsonrpc":"2.0","id":{{{request["id"]}}},"result":{"stopReason":"end_turn"}}""");

        Assert.Equal("end_turn", await prompt.WaitAsync(TimeSpan.FromSeconds(10)));
        var (sessionId, update) = Assert.Single(updates);
        Assert.Equal("s1", sessionId);
        Assert.Equal(new AcpMessageChunk(AcpChunkKind.Agent, "Hello", null), update);
    }

    [Fact]
    public async Task Lists_past_sessions_page_by_page()
    {
        var initialize = _client.InitializeAsync();
        var request = await ReceiveAsync();
        await SendAsync($$$$$$"""{"jsonrpc":"2.0","id":{{{{{{request["id"]}}}}}},"result":{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"sessionCapabilities":{"list":{}},"promptCapabilities":{"image":true}}}}""");
        await initialize.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(_client.CanSendImages);
        Assert.True(_client.CanListSessions);
        Assert.True(_client.CanLoadSessions);

        var list = _client.ListSessionsAsync("/work");
        request = await ReceiveAsync();
        Assert.Equal("session/list", (string?)request["method"]);
        Assert.Equal("/work", (string?)request["params"]!["cwd"]);
        Assert.Null(request["params"]!["cursor"]);
        await SendAsync($$$"""
            {"jsonrpc":"2.0","id":{{{request["id"]}}},"result":{"sessions":[
              {"sessionId":"a","cwd":"/work","title":"Fix the build","updatedAt":"2026-11-16T19:46:38.686Z"},
              {"cwd":"/work","title":"no id, skipped"}],"nextCursor":"page2"}}
            """);
        request = await ReceiveAsync();
        Assert.Equal("page2", (string?)request["params"]!["cursor"]);
        await SendAsync($$$"""{"jsonrpc":"2.0","id":{{{request["id"]}}},"result":{"sessions":[{"sessionId":"b","cwd":"/work"}]}}""");

        var sessions = await list.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, sessions.Count);
        Assert.Equal(new AcpSessionInfo("a", "Fix the build", new DateTimeOffset(2026, 11, 16, 19, 46, 38, 686, TimeSpan.Zero)), sessions[0]);
        Assert.Equal(new AcpSessionInfo("b", null, null), sessions[1]);
    }

    [Fact]
    public async Task An_agent_without_the_capabilities_says_so()
    {
        await HandshakeAsync();

        Assert.False(_client.CanListSessions);
        Assert.False(_client.CanLoadSessions);
    }

    [Fact]
    public async Task Loading_a_session_replays_it_before_answering()
    {
        await HandshakeAsync();
        var updates = new ConcurrentQueue<(string, AcpUpdate)>();
        _client.Updated += (session, update) => updates.Enqueue((session, update));

        var load = _client.LoadSessionAsync("old", "/work");
        var request = await ReceiveAsync();
        Assert.Equal("session/load", (string?)request["method"]);
        Assert.Equal("old", (string?)request["params"]!["sessionId"]);
        Assert.Equal("/work", (string?)request["params"]!["cwd"]);
        await SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"old","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"hi"}}}}""");
        await SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"old","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello"}}}}""");
        await SendAsync($$$"""
            {"jsonrpc":"2.0","id":{{{request["id"]}}},"result":{"configOptions":[
              {"id":"mode","name":"Mode","type":"select","currentValue":"default","options":[{"value":"default","name":"Manual"}]}]}}
            """);

        var session = await load.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("old", session.Id);
        Assert.Equal("mode", Assert.Single(session.Options).Id);
        Assert.Equal(
            [("old", new AcpMessageChunk(AcpChunkKind.User, "hi", null)), ("old", (AcpUpdate)new AcpMessageChunk(AcpChunkKind.Agent, "Hello", null))],
            updates.ToArray());
    }

    [Fact]
    public async Task Answers_a_permission_request_with_the_chosen_option()
    {
        await HandshakeAsync();
        AcpPermissionRequest? asked = null;
        _client.PermissionHandler = request =>
        {
            asked = request;
            return Task.FromResult<string?>("allow-once");
        };

        // claude-agent-acp numbers its own requests from 0.
        await SendAsync("""
            {"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"s1",
             "toolCall":{"toolCallId":"t1","title":"touch made.txt","kind":"execute"},
             "options":[{"optionId":"allow-once","name":"Yes","kind":"allow_once"},{"optionId":"reject","name":"No","kind":"reject_once"}]}}
            """);

        var answer = await ReceiveAsync();
        Assert.Equal(0, (int?)answer["id"]);
        Assert.Equal("selected", (string?)answer["result"]!["outcome"]!["outcome"]);
        Assert.Equal("allow-once", (string?)answer["result"]!["outcome"]!["optionId"]);
        Assert.Equal("touch made.txt", asked!.ToolCall.Title);
    }

    [Fact]
    public async Task A_permission_request_with_no_answer_is_cancelled()
    {
        await HandshakeAsync();
        _client.PermissionHandler = _ => Task.FromResult<string?>(null);

        await SendAsync("""
            {"jsonrpc":"2.0","id":"p-1","method":"session/request_permission","params":{"sessionId":"s1",
             "toolCall":{"toolCallId":"t1"},"options":[{"optionId":"allow-once","name":"Yes","kind":"allow_once"}]}}
            """);

        var answer = await ReceiveAsync();
        Assert.Equal("p-1", (string?)answer["id"]);   // a string id comes back as a string
        Assert.Equal("cancelled", (string?)answer["result"]!["outcome"]!["outcome"]);
    }

    [Fact]
    public async Task An_unknown_request_gets_method_not_found()
    {
        await HandshakeAsync();

        await SendAsync("""{"jsonrpc":"2.0","id":7,"method":"fs/read_text_file","params":{"sessionId":"s1","path":"/a"}}""");

        var answer = await ReceiveAsync();
        Assert.Equal(7, (int?)answer["id"]);
        Assert.Equal(-32601, (int?)answer["error"]!["code"]);
    }

    [Fact]
    public async Task Cancel_is_a_notification()
    {
        await HandshakeAsync();

        _client.Cancel("s1");

        var message = await ReceiveAsync();
        Assert.Equal("session/cancel", (string?)message["method"]);
        Assert.Null(message["id"]);
        Assert.Equal("s1", (string?)message["params"]!["sessionId"]);
    }

    [Fact]
    public async Task An_error_response_throws()
    {
        await HandshakeAsync();

        var setting = _client.SetConfigOptionAsync("s1", "model", "nope");
        var request = await ReceiveAsync();
        Assert.Equal("session/set_config_option", (string?)request["method"]);
        await SendAsync($$$"""{"jsonrpc":"2.0","id":{{{request["id"]}}},"error":{"code":-32602,"message":"Unknown model"}}""");

        var error = await Assert.ThrowsAsync<AcpException>(() => setting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Unknown model", error.Message);
        Assert.Equal(-32602, error.Code);
        Assert.False(error.IsAuthRequired);
    }

    [Fact]
    public async Task An_expired_login_is_an_auth_required_error()
    {
        await HandshakeAsync();

        var prompt = _client.PromptAsync("s1", [new AcpPromptBlock.Text("hi")]);
        var request = await ReceiveAsync();
        await SendAsync($$$"""{"jsonrpc":"2.0","id":{{{request["id"]}}},"error":{"code":-32000,"message":"Authentication required"}}""");

        var error = await Assert.ThrowsAsync<AcpException>(() => prompt.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(error.IsAuthRequired);
    }

    [Fact]
    public async Task The_agent_exiting_fails_the_waiting_requests()
    {
        await HandshakeAsync();
        var prompt = _client.PromptAsync("s1", [new AcpPromptBlock.Text("hi")]);
        await ReceiveAsync();

        _toClient.Dispose();   // the agent's stdout closes

        await Assert.ThrowsAsync<AcpException>(() => prompt.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Reports_the_sign_in_state()
    {
        await HandshakeAsync();
        var signedIn = new TaskCompletionSource<string>();
        _client.AuthStateChanged += state => signedIn.TrySetResult(state);

        await SendAsync("""{"jsonrpc":"2.0","method":"auth/status_update","params":{"state":"unauthenticated","accounts":[]}}""");

        Assert.Equal("unauthenticated", await signedIn.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("unauthenticated", _client.AuthState);
    }
}

/// <summary>The sign-in methods in initialize's answer, as claude-agent-acp 0.107 sends them.</summary>
public sealed class AcpAuthMethodTests
{
    private static IReadOnlyList<AcpAuthMethod> Parse(string json, string? agent = null)
    {
        using var document = JsonDocument.Parse(json);
        return AcpParser.AuthMethods(document.RootElement, agent);
    }

    [Fact]
    public void Reads_the_terminal_auth_commands()
    {
        var methods = Parse("""
            {"protocolVersion":1,"authMethods":[
             {"description":"Use Claude subscription ","name":"Claude Subscription","id":"claude-ai-login","type":"terminal","args":["--cli","auth","login","--claudeai"],
              "_meta":{"terminal-auth":{"command":"/opt/claude-agent-acp","args":["--cli","auth","login","--claudeai"],"label":"Claude Login"}}},
             {"description":"Use Anthropic Console (API usage billing)","name":"Anthropic Console","id":"console-login","type":"terminal","args":["--cli","auth","login","--console"],
              "_meta":{"terminal-auth":{"command":"/opt/claude-agent-acp","args":["--cli","auth","login","--console"],"label":"Anthropic Console Login"}}}]}
            """);

        Assert.Equal(2, methods.Count);
        Assert.Equal("claude-ai-login", methods[0].Id);
        Assert.Equal("Claude Subscription", methods[0].Name);
        Assert.Equal("Use Claude subscription", methods[0].Description);
        Assert.Equal("/opt/claude-agent-acp", methods[0].Command);
        Assert.Equal(["--cli", "auth", "login", "--claudeai"], methods[0].Args);
        Assert.Equal("console-login", methods[1].Id);
    }

    [Fact]
    public void A_terminal_method_without_a_command_runs_the_agent_itself()
    {
        var methods = Parse("""{"authMethods":[{"name":"Login","id":"login","type":"terminal","args":["login"]}]}""", agent: "/opt/agent");

        Assert.Equal("/opt/agent", Assert.Single(methods).Command);
        Assert.Equal(["login"], methods[0].Args);
    }

    [Fact]
    public void Methods_the_IDE_cant_run_are_left_out()
    {
        Assert.Empty(Parse("""{"authMethods":[{"name":"API key","id":"key","description":"Set ANTHROPIC_API_KEY"}]}"""));
        Assert.Empty(Parse("""{"authMethods":[]}"""));
        Assert.Empty(Parse("""{"protocolVersion":1}"""));
    }
}

/// <summary>Running a sign-in command, with a shell script standing in for "claude auth login".</summary>
public sealed class AgentSignInTests
{
    [Theory]
    [InlineData("If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true&state=x", "https://claude.com/cai/oauth/authorize?code=true&state=x")]
    [InlineData("Opening browser to sign in…", null)]
    public void Finds_the_link_to_open_by_hand(string line, string? link)
        => Assert.Equal(link, AgentSignIn.FindLink(line));

    private static AcpAuthMethod Script(string script) => new("login", "Login", null, "/bin/sh", ["-c", script]);

    [Fact]
    public async Task Signs_in_with_a_pasted_code()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var signIn = AgentSignIn.Start(Script("""echo "visit: https://example.com/login"; printf 'Paste code here if prompted > '; read code; [ "$code" = "abc" ]"""));
        var link = new TaskCompletionSource<string>();
        signIn.LinkFound += found => link.TrySetResult(found);

        Assert.Equal("https://example.com/login", await link.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        signIn.SubmitCode("  abc \n");
        Assert.True(await signIn.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_failed_sign_in_says_why()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var signIn = AgentSignIn.Start(Script("echo 'Login failed: denied' >&2; exit 1"));

        Assert.False(await signIn.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(100);   // stderr's last line can land just after the exit
        Assert.Contains("denied", signIn.RecentOutput);
    }

    [Fact]
    public async Task Cancelling_stops_the_command()
    {
        if (OperatingSystem.IsWindows())
            return;
        var signIn = AgentSignIn.Start(Script("sleep 30"));

        signIn.Dispose();

        Assert.False(await signIn.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}

public sealed class ClaudeAgentInstallTests
{
    [Theory]
    [InlineData("LINUX", Architecture.X64, false, "claude-agent-acp-linux-x64.tar.gz")]
    [InlineData("LINUX", Architecture.Arm64, true, "claude-agent-acp-linux-arm64-musl.tar.gz")]
    [InlineData("OSX", Architecture.Arm64, false, "claude-agent-acp-darwin-arm64.zip")]
    [InlineData("WINDOWS", Architecture.X64, false, "claude-agent-acp-windows-x64.zip")]
    [InlineData("LINUX", Architecture.X86, false, null)]
    public void Picks_the_release_file_for_the_platform(string os, Architecture architecture, bool musl, string? expected)
        => Assert.Equal(expected, ClaudeAgentInstall.AssetName(OSPlatform.Create(os), architecture, musl));

    [Fact]
    public void Reads_the_release_from_GitHubs_json()
    {
        using var document = JsonDocument.Parse("""
            {"tag_name":"v0.107.2","assets":[
              {"name":"claude-agent-acp-darwin-arm64.zip","size":1,"browser_download_url":"https://example.com/mac.zip"},
              {"name":"claude-agent-acp-linux-x64.tar.gz","size":219945814,"browser_download_url":"https://example.com/linux.tar.gz"}]}
            """);

        Assert.Equal(new ClaudeAgentRelease("0.107.2", "claude-agent-acp-linux-x64.tar.gz", "https://example.com/linux.tar.gz", 219945814),
            ClaudeAgentInstall.ReleaseFrom(document.RootElement, "claude-agent-acp-linux-x64.tar.gz"));
        Assert.Null(ClaudeAgentInstall.ReleaseFrom(document.RootElement, "claude-agent-acp-windows-x64.zip"));
    }

    [Fact]
    public void Skips_a_release_whose_files_are_still_being_built_and_prereleases()
    {
        using var document = JsonDocument.Parse("""
            [{"tag_name":"v0.108.0","prerelease":true,"assets":[{"name":"a.tar.gz","size":1,"browser_download_url":"https://example.com/pre"}]},
             {"tag_name":"v0.107.3","prerelease":false,"draft":false,"assets":[]},
             {"tag_name":"v0.107.2","prerelease":false,"draft":false,"assets":[{"name":"a.tar.gz","size":2,"browser_download_url":"https://example.com/ok"}]}]
            """);

        Assert.Equal("0.107.2", ClaudeAgentInstall.NewestWith(document.RootElement, "a.tar.gz")?.Version);
    }
}

public sealed class CommitMessageWriterTests
{
    [Fact]
    public void The_prompt_has_the_style_the_diff_and_the_new_files()
    {
        var prompt = CommitMessageWriter.BuildPrompt(["fix: one", "feat: two"], "diff --git a/x b/x\n", ["src/New.cs"]);

        Assert.Equal(
            "Recent commit subjects in this repository (match their style):\n- fix: one\n- feat: two\n\n" +
            "The change:\ndiff --git a/x b/x\n\n" +
            "New files being added:\n- src/New.cs\n",
            prompt.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_huge_diff_is_cut()
    {
        var prompt = CommitMessageWriter.BuildPrompt([], new string('x', CommitMessageWriter.MaxDiffLength + 500), []);

        Assert.Contains("[... 500 more characters of diff left out]", prompt);
        Assert.True(prompt.Length < CommitMessageWriter.MaxDiffLength + 200);
    }

    [Theory]
    [InlineData("  fix: a thing\n", "fix: a thing")]
    [InlineData("```\nfix: a thing\n\nBody.\n```", "fix: a thing\n\nBody.")]
    [InlineData("```text\r\nfix: a thing\r\n```\r\n", "fix: a thing")]
    public void The_reply_is_cleaned(string reply, string expected) => Assert.Equal(expected, CommitMessageWriter.Clean(reply));
}

public sealed class InlineDiffTests
{
    [Fact]
    public void Removed_lines_come_before_the_lines_that_replace_them()
    {
        var lines = AXamlIDE.Services.Git.InlineDiff.Compare("a\nb\nc", "a\nB\nc");

        Assert.Equal(["  a", "- b", "+ B", "  c"], lines.Select(Show));
    }

    [Fact]
    public void A_new_file_is_all_added()
    {
        Assert.Equal(["+ x", "+ y"], AXamlIDE.Services.Git.InlineDiff.Compare(null, "x\ny").Select(Show));
    }

    private static string Show(AXamlIDE.Services.Git.DiffLine line) => line.Kind switch
    {
        AXamlIDE.Services.Git.DiffLineKind.Added => "+ ",
        AXamlIDE.Services.Git.DiffLineKind.Removed => "- ",
        _ => "  "
    } + line.Text;
}
