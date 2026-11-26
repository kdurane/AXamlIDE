using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Preview;

namespace AXamlIDE.Services.Mcp;

/// <summary>
/// ui_open, ui_tree, ui_act and ui_screenshot: a view of the user's app running live in a headless window
/// (<see cref="McpUiHost"/>), to click and type in and read back as text, which costs far less context than pictures.
/// </summary>
public sealed partial class IdeMcpServer
{
    private const string TreeFormat =
        "Each line: @id Type #Name \"text\" [states] .classes; a button or row holding only text and icons is one line with its " +
        "texts; an icon button shows its tooltip. Positions and sizes: ui_tree with layout.";

    private const string TargetHelp =
        "The control: @id from a tree (best), #Name, a type (Button), a type and its text (Button \"Save\"), or \"text\".";

    private McpTool UiOpen()
    {
        var schema = McpArguments.Schema(
            ("path", "string", "The view's .axaml file (it needs an x:Class).", null),
            ("dataContext", "string", "What to bind it to: a type (made with new()) or a static member, e.g. MyApp.SampleData.DesignData.Main. " +
                "Default: the file's Design.DataContext ({x:Static ...} or an element), else what the view sets itself.", null),
            ("width", "number", "Window size in DIPs. Default: the window's own, else d:DesignWidth, else 1000.", null),
            ("height", "number", "Default: the window's own, else d:DesignHeight, else 700.", null),
            ("theme", "string", "Theme variant. Default: the app's default.", ["Light", "Dark"]));
        schema["required"] = new JsonArray("path");
        return new McpTool("ui_open",
            "Run a view of the user's app live, in a hidden headless window (the app's real code, last build; no screen and " +
            "no real input, so nothing reaches the user's desktop), and return its control tree. It stays open for ui_act, " +
            "ui_tree and ui_screenshot until the next ui_open. The app's startup code doesn't run, so the view gets only " +
            "what dataContext gives it. " + TreeFormat,
            schema,
            (arguments, ct) => OnUiThread(() => UiOpenAsync(arguments, ct)));
    }

    private McpTool UiTree() => new("ui_tree",
        "The control tree of what ui_open opened (and any windows or popups it has opened since). " + TreeFormat,
        McpArguments.Schema(
            ("target", "string", "Only this control's subtree. " + TargetHelp, null),
            ("depth", "number", "Levels to show below the root(s).", null),
            ("props", "string", "Comma-separated properties to add to each line that has them: Width, Background, Grid.Row, " +
                "DataContext.Title, IsEnabled...", null),
            ("layout", "boolean", "Add each control's position in its window and size: x,y WxH in DIPs.", null),
            ("all", "boolean", "The full visual tree: template parts, hidden controls, nothing folded.", null)),
        (arguments, ct) => OnUiThread(() => UiRequestAsync("tree", arguments, ct)));

    private McpTool UiAct() => new("ui_act",
        "Do one thing in the view ui_open opened, as a user would (mouse and keyboard go to the app's own headless window), " +
        "wait a moment, and return what changed in the control tree: + added, - removed, ~ changed (with what it was). " +
        "It says when the target is disabled or covered by something else.",
        McpArguments.Schema(
            ("action", "string", "click, double_click, right_click, hover, press (and hold), release, drag (to `to` or dx/dy), " +
                "scroll (dy lines, + is down), type (text), key (keys), focus, or wait (ms).",
                ["click", "double_click", "right_click", "hover", "press", "release", "drag", "scroll", "type", "key", "focus", "wait"]),
            ("target", "string", TargetHelp + " type and key focus it first; without one they go to what has focus.", null),
            ("x", "number", "With a target: a point inside it (DIPs from its top-left) instead of its centre. Without: in the newest window.", null),
            ("y", "number", "See x.", null),
            ("text", "string", "For type: the text, typed at the caret (key Ctrl+A first to replace what's there).", null),
            ("keys", "string", "For key: combinations separated by spaces, e.g. \"Ctrl+S\", \"Tab Tab Enter\", \"Shift+F10\".", null),
            ("to", "string", "For drag: the control to drop on.", null),
            ("dx", "number", "For drag and scroll.", null),
            ("dy", "number", "For drag and scroll.", null),
            ("modifiers", "string", "Keys held during a mouse action, e.g. \"Ctrl\" or \"Ctrl+Shift\".", null),
            ("ms", "number", "How long to wait afterwards for the app to react (default 200; wait: 500). Tooltips need ~600 after hover.", null)),
        (arguments, ct) => OnUiThread(() => UiRequestAsync("act", arguments, ct)));

    private McpTool UiScreenshot() => new("ui_screenshot",
        "A picture of what ui_open opened (the newest window), or of one control: for how it looks, when the tree's text isn't enough.",
        McpArguments.Schema(
            ("target", "string", TargetHelp, null),
            ("maxSize", "number", "Shrink to fit this many pixels on the longer side. Default 800.", null)),
        (arguments, ct) => OnUiThread(() => UiRequestAsync("screenshot", arguments, ct)));

    private async Task<McpToolResult> UiOpenAsync(JsonElement arguments, CancellationToken ct)
    {
        if (ResolvePath(McpArguments.String(arguments, "path")) is not { } path || !path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            return McpToolResult.Error("Give the path of the view's .axaml file.");
        if (await TextOfAsync(path, ct) is not { } text)
            return McpToolResult.Error($"{Relative(path)} doesn't exist.");

        var design = AxamlDesignInfo.Read(text);
        if (design.ClassName is null)
            return McpToolResult.Error($"{Relative(path)} has no x:Class, so there's no class to make: only views with code-behind can be opened.");

        var request = new JsonObject
        {
            ["class"] = design.ClassName,
            ["dataContext"] = McpArguments.String(arguments, "dataContext") ?? design.DataContext,
            ["width"] = McpArguments.Number(arguments, "width") ?? design.Width,
            ["height"] = McpArguments.Number(arguments, "height") ?? design.Height,
            ["theme"] = McpArguments.String(arguments, "theme")
        };

        McpUiHost.Reply reply;
        try
        {
            reply = await _uiHost.OpenAsync(path, request, ct);
        }
        catch (InvalidOperationException ex)
        {
            return McpToolResult.Error($"{Relative(path)} couldn't be opened: {ex.Message}");
        }

        var stale = FindOpen(path) is { IsDirty: true }
            ? "Note: the editor has unsaved changes to this file; this is the last build."
            : await IsNewerThanBuildAsync(path) ? "Note: the file changed after the last build (the build tool brings it up to date)." : null;
        return UiResult(reply, stale);
    }

    private async Task<bool> IsNewerThanBuildAsync(string path)
    {
        try
        {
            var target = await _xaml.GetPreviewTargetAsync(path);
            return File.GetLastWriteTimeUtc(path) > target.XamlAssemblyLastWriteUtc;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<McpToolResult> UiRequestAsync(string command, JsonElement arguments, CancellationToken ct)
    {
        var request = JsonNode.Parse(arguments.ValueKind == JsonValueKind.Object ? arguments.GetRawText() : "{}")!.AsObject();
        request["command"] = command;
        return UiResult(await _uiHost.RequestAsync(request, ct), null);
    }

    /// <summary>The reply's text or picture, with what the app threw or wrote meanwhile (the model wouldn't see that otherwise).</summary>
    private static McpToolResult UiResult(McpUiHost.Reply reply, string? note)
    {
        var text = new StringBuilder(reply.Error ?? reply.Text ?? "");
        foreach (var extra in new[] { reply.Note, note })
        {
            if (extra is not null)
                text.Append('\n').Append(extra);
        }
        if (reply.Exceptions.Count > 0)
            text.Append("\nThe app threw (unhandled, on its UI thread):\n").AppendJoin('\n', reply.Exceptions.Take(5));
        if (reply.Output.Count > 0)
            text.Append("\nThe app wrote:\n").AppendJoin('\n', reply.Output.TakeLast(20));

        if (reply.Error is not null)
            return McpToolResult.Error(text.ToString());
        return reply.Png is { } png
            ? new McpToolResult([new McpContent.Text(text.ToString()), new McpContent.Png(png)])
            : new McpToolResult([new McpContent.Text(text.ToString())]);
    }
}
