using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace AXamlIDE.UiHost;

/// <summary>
/// What the IDE asks for, one request at a time on the UI thread: open (a view in a new headless window), tree, act
/// (UiSession.Actions.cs) and screenshot. The windows stay open between requests, so a click's effects are there for the
/// next one. Controls are pointed at by the selectors in UiSession.Targets.cs.
/// </summary>
internal sealed partial class UiSession
{
    private const int DefaultTreeLimit = 250;

    private readonly Assembly _app;
    private readonly UiTree _tree = new();
    /// <summary>Every open window (the opened view's and any the app opens, dialogs included), oldest first.</summary>
    private readonly List<Window> _windows = [];
    private readonly List<string> _exceptions = [];

    public UiSession(Assembly app)
    {
        _app = app;
        Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) =>
        {
            if (sender is Window window && !_windows.Contains(window))
                _windows.Add(window);
        });
        Window.WindowClosedEvent.AddClassHandler(typeof(Window), (sender, _) =>
        {
            if (sender is Window window)
                _windows.Remove(window);
        });
    }

    public void NoteException(Exception ex) => _exceptions.Add(Program.Describe(ex));

    /// <summary>Unhandled exceptions since the last reply go out with this one.</summary>
    public void AddExceptions(JsonObject reply)
    {
        if (_exceptions.Count == 0)
            return;
        reply["exceptions"] = new JsonArray([.. _exceptions.Select(e => (JsonNode)e)]);
        _exceptions.Clear();
    }

    public Task<JsonObject> HandleAsync(JsonObject request) => (string?)request["command"] switch
    {
        "open" => OpenAsync(request),
        "tree" => Task.FromResult(Tree(request)),
        "act" => ActAsync(request),
        "screenshot" => Task.FromResult(Screenshot(request)),
        var other => throw new UiException($"Unknown command {other}.")
    };

    // ------------------------------------------------------------ open

    private async Task<JsonObject> OpenAsync(JsonObject request)
    {
        var className = (string?)request["class"] ?? throw new UiException("No class given.");
        foreach (var open in _windows.ToList())
            open.Close();
        _windows.Clear();

        if (request["assembly"] is { } assemblyPath)
            Assembly.Load(AssemblyName.GetAssemblyName((string)assemblyPath!));
        var type = FindType(className) ?? throw new UiException($"No class {className} in the app's assemblies (built?).");
        if (!typeof(Control).IsAssignableFrom(type))
            return Text($"{className} is a {type.BaseType?.Name}, not a control: only controls and windows can be opened.");

        Application.Current!.RequestedThemeVariant = (string?)request["theme"] switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        Control view;
        try
        {
            view = (Control)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            return Text($"new {type.Name}() threw {Program.Describe(ex)}");
        }

        string dataContext;
        if ((string?)request["dataContext"] is { Length: > 0 } given)
        {
            view.DataContext = CreateValue(given);
            dataContext = $"{view.DataContext?.GetType().Name ?? "null"} (from {given})";
        }
        else if (view.DataContext is { } own)
            dataContext = $"{own.GetType().Name} (the view sets it)";
        else if (Design.GetDataContext(view) is { } design)
        {
            view.DataContext = design;
            dataContext = $"{design.GetType().Name} (its Design.DataContext)";
        }
        else
            dataContext = "none, so bindings show nothing (give dataContext)";

        var window = view as Window ?? new Window { Content = view, Title = type.Name };
        window.Width = Number(request["width"]) ?? Size(window.Width, Design.GetWidth(view), 1000);
        window.Height = Number(request["height"]) ?? Size(window.Height, Design.GetHeight(view), 700);
        window.Show();
        await SettleAsync(400);

        var header = $"Opened {type.Name} in a headless {window.Width:0}x{window.Height:0} window; DataContext: {dataContext}.";
        return Text(header + "\n" + TreeText(_windows, new UiTree.Options(), DefaultTreeLimit));

        static double Size(double own, double design, double fallback)
            => double.IsFinite(own) && own > 0 ? own : double.IsFinite(design) && design > 0 ? design : fallback;
    }

    // ------------------------------------------------------------ tree

    private JsonObject Tree(JsonObject request)
    {
        RequireWindow();
        var props = ((string?)request["props"])?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var options = new UiTree.Options(
            All: request["all"] is JsonValue all && all.TryGetValue(out bool yes) && yes,
            MaxDepth: (int?)Number(request["depth"]) ?? int.MaxValue,
            Props: props,
            Layout: request["layout"] is JsonValue layout && layout.TryGetValue(out bool withLayout) && withLayout);
        IEnumerable<Visual> roots = (string?)request["target"] is { Length: > 0 } target ? [Resolve(target)] : _windows;
        return Text(TreeText(roots, options, (int?)Number(request["limit"]) ?? DefaultTreeLimit));
    }

    private string TreeText(IEnumerable<Visual> roots, UiTree.Options options, int limit)
    {
        var lines = _tree.Lines(roots, options);
        var text = new StringBuilder();
        var baseDepth = lines.Count > 0 ? lines.Min(l => l.Depth) : 0;
        foreach (var line in lines.Take(limit))
            text.Append(' ', 2 * (line.Depth - baseDepth)).AppendLine(line.Text);
        if (lines.Count > limit)
            text.AppendLine($"… {lines.Count - limit} more (narrow it down with target or depth).");
        return text.ToString().TrimEnd();
    }

    // ------------------------------------------------------------ screenshot

    private JsonObject Screenshot(JsonObject request)
    {
        RequireWindow();
        Visual visual = (string?)request["target"] is { Length: > 0 } target ? Resolve(target) : _windows[^1];
        var maxSize = Number(request["maxSize"]) ?? 800;
        var (width, height) = (visual.Bounds.Width, visual.Bounds.Height);
        if (width < 1 || height < 1)
            return Text($"{_tree.Short(visual)} has no size ({width:0}x{height:0}), so there's nothing to see.");

        var scale = Math.Min(1, maxSize / Math.Max(width, height));
        var pixels = new PixelSize(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
        using var png = new MemoryStream();
        if (visual is TopLevel topLevel)
        {
            // The window as last drawn (popups, focus rectangles and all in their places), shrunk.
            using var frame = Headless.CaptureRenderedFrame(topLevel);
            if (scale >= 1)
                frame.Save(png);
            else
            {
                using var full = new MemoryStream();   // a headless frame can't be resized as it is: decode it smaller
                frame.Save(full);
                full.Position = 0;
                using var scaled = Bitmap.DecodeToWidth(full, pixels.Width);
                scaled.Save(png);
            }
        }
        else
        {
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
            bitmap.Render(visual);
            bitmap.Save(png);
        }
        var reply = Text($"{_tree.Short(visual)}, {pixels.Width}x{pixels.Height} px.");
        reply["png"] = Convert.ToBase64String(png.ToArray());
        return reply;
    }

    // ------------------------------------------------------------ helpers

    private void RequireWindow()
    {
        if (_windows.Count == 0)
            throw new UiException("No window is open: call ui_open first.");
    }

    /// <summary>Lets the app catch up (bindings, layout, rendering, short animations) for about this long.</summary>
    private static Task SettleAsync(int milliseconds) => Task.Delay(Math.Clamp(milliseconds, 0, 10_000));

    private static JsonObject Text(string text) => new() { ["text"] = text };

    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number) ? number : null;

    /// <summary>A type of the app's by full name, or by its short name when only one type has it.</summary>
    private Type? FindType(string name)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(IsAppAssembly).Prepend(_app).Distinct().ToList();
        foreach (var assembly in assemblies)
        {
            if (assembly.GetType(name) is { } type)
                return type;
        }
        var matches = assemblies.SelectMany(Types).Where(t => t.Name == name).ToList();
        return matches.Count == 1 ? matches[0] : null;

        static IEnumerable<Type> Types(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.OfType<Type>();
            }
        }
    }

    private static bool IsAppAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "";
        return !assembly.IsDynamic && !name.StartsWith("System", StringComparison.Ordinal) && !name.StartsWith("Microsoft", StringComparison.Ordinal)
               && !name.StartsWith("Avalonia", StringComparison.Ordinal) && name != "mscorlib" && name != "netstandard"
               && name != typeof(UiSession).Assembly.GetName().Name;
    }

    /// <summary>
    /// A value from text: a type with a parameterless constructor ("MyApp.ViewModels.MainViewModel"), or a static
    /// property, field or parameterless method ("MyApp.SampleData.DesignData.Main").
    /// </summary>
    private object? CreateValue(string text)
    {
        if (FindType(text) is { } type)
            return Activator.CreateInstance(type);

        var dot = text.LastIndexOf('.');
        if (dot > 0 && FindType(text[..dot]) is { } owner)
        {
            var member = text[(dot + 1)..];
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            if (owner.GetProperty(member, flags) is { } property)
                return property.GetValue(null);
            if (owner.GetField(member, flags) is { } field)
                return field.GetValue(null);
            if (owner.GetMethod(member, flags, Type.EmptyTypes) is { } method)
                return method.Invoke(null, null);
        }
        throw new UiException($"No type or static member {text} in the app's assemblies.");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A request that can't be done as asked (bad selector, missing argument): its message is the whole reply.</summary>
internal sealed class UiException(string message) : Exception(message);
