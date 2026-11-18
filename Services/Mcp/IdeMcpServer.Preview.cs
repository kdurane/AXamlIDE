using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Services.Preview;

namespace AXamlIDE.Services.Mcp;

/// <summary>render_xaml: an .axaml file as a picture, through render_xaml's own designer host.</summary>
public sealed partial class IdeMcpServer
{
    private McpTool RenderXaml()
    {
        var schema = McpArguments.Schema(
            ("path", "string", "The .axaml file (absolute, or relative to the workspace folder).", null),
            ("xaml", "string", "XAML to render in that file's place, to try a change without editing it. Default: the editor's text, or the file's.", null),
            ("theme", "string", "Wrap the content in this theme variant. Default: none (the app's). Brushes the app defines in " +
                "its Application resources keep the app's theme (only theme-dictionary and Fluent resources follow this).", ["Light", "Dark"]),
            ("scale", "number", "Render scaling, 1 = 96 DPI. Default 1.", null),
            ("maxSize", "number", "Shrink the picture to fit this many pixels on its longer side. Default 800.", null),
            ("pointer", "string", "Do this with the mouse at (x, y) before the picture is taken: hover (pointer-over styles, " +
                "tooltips stay closed), press (held down: pressed styles) or click (press and let go).", ["hover", "press", "click"]),
            ("x", "number", "The pointer's x in DIPs from the left (a pixel of an unshrunk render at scale 1).", null),
            ("y", "number", "The pointer's y in DIPs from the top.", null));
        schema["required"] = new JsonArray("path");
        return new McpTool("render_xaml",
            "Render an .axaml file with the Avalonia previewer and return it as a PNG, or its XAML error (with the line). " +
            "The project must have been built (the build tool does that). Size the root with d:DesignWidth/d:DesignHeight; " +
            "bindings show nothing without Design.DataContext. The pointer only reaches Avalonia's input, not the OS window chrome.",
            schema,
            (arguments, ct) => OnUiThread(() => RenderXamlAsync(arguments, ct)));
    }

    private async Task<McpToolResult> RenderXamlAsync(JsonElement arguments, CancellationToken ct)
    {
        if (ResolvePath(McpArguments.String(arguments, "path")) is not { } path)
            return McpToolResult.Error("Give the path of an .axaml file.");
        if (!path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            return McpToolResult.Error($"{Relative(path)} isn't an .axaml file.");

        var open = FindOpen(path);
        var text = McpArguments.String(arguments, "xaml") ?? await TextOfAsync(path, ct);
        if (text is null)
            return McpToolResult.Error($"{Relative(path)} doesn't exist.");

        var theme = McpArguments.String(arguments, "theme") ?? "";
        var scale = Math.Clamp(McpArguments.Number(arguments, "scale") ?? 1, 0.25, 4);
        var maxSize = (int)Math.Clamp(McpArguments.Number(arguments, "maxSize") ?? 800, 64, 2000);

        McpPreviewRenderer.Pointer? pointer = null;
        if (McpArguments.String(arguments, "pointer") is { } action)
        {
            if (McpArguments.Number(arguments, "x") is not { } x || McpArguments.Number(arguments, "y") is not { } y)
                return McpToolResult.Error("A pointer action needs x and y.");
            pointer = new McpPreviewRenderer.Pointer(action, x, y);
        }

        var result = await _renderer.RenderAsync(path, PreviewThemeVariant.Apply(text, theme), scale, maxSize, pointer, ct);
        if (result.Error is { } error)
        {
            var where = error.Line is { } line ? $" (line {line}{(error.Column is { } column ? $", column {column}" : "")})" : "";
            return McpToolResult.Error($"{Relative(path)} didn't render{where}: {error.Message}");
        }

        var source = McpArguments.String(arguments, "xaml") is not null ? "the given XAML"
            : open is { IsDirty: true } ? "the editor's unsaved text"
            : "the saved file";
        return new McpToolResult([
            new McpContent.Text($"{Relative(path)} rendered from {source}, {result.Width}x{result.Height} px" +
                (pointer is null ? "." : $", after {pointer.Action} at ({pointer.X}, {pointer.Y}).")),
            new McpContent.Png(result.Png!)
        ]);
    }
}
