using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Threading;

namespace AXamlIDE.UiHost;

/// <summary>
/// Started by the IDE as: dotnet exec --runtimeconfig app.runtimeconfig.json --depsfile app.deps.json
/// AXamlIDE.UiHost.dll --app app.dll --headless Avalonia.Headless.dll [--probe folder]...
///
/// It sets the app up the way the Avalonia previewer does (the app's BuildAvaloniaApp, with the headless platform
/// swapped in, and without OnFrameworkInitializationCompleted, so the app's startup code doesn't run), then reads
/// one JSON request per line from stdin and writes one JSON reply per line to stdout. The app's own console output
/// goes to stderr, which the IDE keeps as the app's output. The host stops when stdin closes, so it never outlives
/// the IDE.
/// </summary>
public static class Program
{
    private static TextWriter _replies = TextWriter.Null;

    public static int Main(string[] args)
    {
        _replies = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(Console.Error);

        string? app = null, headless = null;
        var probe = new List<string>();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--app": app = args[i + 1]; break;
                case "--headless": headless = args[i + 1]; break;
                case "--probe": probe.Add(args[i + 1]); break;
            }
        }
        if (app is null || headless is null)
        {
            Reply(new JsonObject { ["error"] = "Usage: AXamlIDE.UiHost --app <app.dll> --headless <Avalonia.Headless.dll> [--probe <folder>]..." });
            return 2;
        }

        // Avalonia.Headless and anything it needs that the app doesn't ship come from the folders the IDE gave.
        probe.Insert(0, Path.GetDirectoryName(Path.GetFullPath(headless))!);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var folder in probe)
            {
                var file = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(file))
                    return context.LoadFromAssemblyPath(file);
            }
            return null;
        };

        UiSession session;
        try
        {
            session = Start(app);
        }
        catch (Exception ex)
        {
            Reply(new JsonObject { ["error"] = "The app couldn't be set up headless: " + Describe(ex) });
            return 1;
        }

        var stop = new CancellationTokenSource();
        var reader = new Thread(() =>
        {
            while (Console.In.ReadLine() is { } line)
                Dispatcher.UIThread.Post(() => _ = HandleAsync(session, line));
            Dispatcher.UIThread.Post(stop.Cancel);
        }) { IsBackground = true, Name = "requests" };
        reader.Start();

        Reply(new JsonObject { ["ready"] = true });
        Dispatcher.UIThread.MainLoop(stop.Token);
        return 0;
    }

    private static UiSession Start(string appPath)
    {
        var app = Assembly.Load(AssemblyName.GetAssemblyName(appPath));
        var builder = AppBuilderOf(app);
        builder = Headless.Use(builder);
        builder.SetupWithoutStarting();

        var session = _session = new UiSession(app);
        CatchUnhandled();
        Headless.StartRenderTicks();
        return session;
    }

    private static UiSession? _session;

    /// <summary>
    /// The app's unhandled UI-thread exceptions are kept for the next reply, and handled so the app carries on.
    /// Dispatcher.UnhandledException came in Avalonia 11.1, hence reflection.
    /// </summary>
    private static void CatchUnhandled()
    {
        var dispatcher = Dispatcher.UIThread;
        if (dispatcher.GetType().GetEvent("UnhandledException") is not { EventHandlerType: { } handlerType } unhandled)
            return;
        var handler = Delegate.CreateDelegate(handlerType, typeof(Program).GetMethod(nameof(OnUnhandled), BindingFlags.NonPublic | BindingFlags.Static)!);
        unhandled.AddEventHandler(dispatcher, handler);
    }

    private static void OnUnhandled(object? sender, EventArgs e)
    {
        if (e.GetType().GetProperty("Exception")?.GetValue(e) is Exception exception)
            _session?.NoteException(exception);
        e.GetType().GetProperty("Handled")?.SetValue(e, true);
    }

    /// <summary>The app's own BuildAvaloniaApp() (fonts, ReactiveUI and the like set up there), as the previewer uses; else just its Application.</summary>
    private static AppBuilder AppBuilderOf(Assembly app)
    {
        var build = app.EntryPoint?.DeclaringType?.GetMethod("BuildAvaloniaApp",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);
        if (build?.Invoke(null, null) is AppBuilder built)
            return built;

        var applicationType = app.GetTypes().FirstOrDefault(t => typeof(Application).IsAssignableFrom(t) && !t.IsAbstract)
            ?? throw new InvalidOperationException($"{app.GetName().Name} has no BuildAvaloniaApp() and no Application class.");
        return AppBuilder.Configure(() => (Application)Activator.CreateInstance(applicationType)!);
    }

    private static async Task HandleAsync(UiSession session, string line)
    {
        JsonNode? id = null;
        JsonObject reply;
        try
        {
            var request = JsonNode.Parse(line)!.AsObject();
            id = request["id"]?.DeepClone();
            reply = await session.HandleAsync(request);
        }
        catch (UiException ex)
        {
            reply = new JsonObject { ["error"] = ex.Message };
        }
        catch (Exception ex)
        {
            reply = new JsonObject { ["error"] = Describe(ex) };
        }
        session.AddExceptions(reply);
        reply["id"] = id;
        Reply(reply);
    }

    private static void Reply(JsonObject reply)
    {
        lock (_replies)
            _replies.WriteLine(reply.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    /// <summary>The exception that matters (not the reflection wrapper), with where it came from.</summary>
    public static string Describe(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner })
            ex = inner;
        var frame = ex.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return $"{ex.GetType().Name}: {ex.Message}" + (frame is null ? "" : $" ({frame})");
    }
}
