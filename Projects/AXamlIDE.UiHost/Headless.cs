using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AXamlIDE.UiHost;

/// <summary>
/// Avalonia.Headless, called by reflection: it has to be the app's own Avalonia version, and its methods' parameters
/// differ between versions (KeyPress gained a physical key and a key symbol in 11.1, for one). Each call takes the
/// overload by name and fills its parameters by type, leaving the rest at their defaults.
///
/// Headless means: windows are drawn into memory (Skia, as in the real app), not shown, and input only exists as these
/// calls, which go straight into the window's own input handling. Nothing reaches the OS, the screen or the IDE.
/// </summary>
internal static class Headless
{
    private static Assembly? _assembly;

    private static Assembly Assembly => _assembly ??= Assembly.Load("Avalonia.Headless");

    private static Type TypeOf(string name)
        => Assembly.GetType(name) ?? throw new MissingMemberException($"Avalonia.Headless has no {name}.");

    /// <summary>The app's builder with the headless platform (and Skia, so pictures look like the app) in place of the desktop's.</summary>
    public static AppBuilder Use(AppBuilder builder)
    {
        var optionsType = TypeOf("Avalonia.Headless.AvaloniaHeadlessPlatformOptions");
        var options = Activator.CreateInstance(optionsType)!;
        optionsType.GetProperty("UseHeadlessDrawing")?.SetValue(options, false);
        builder = (AppBuilder)Call(TypeOf("Avalonia.Headless.AvaloniaHeadlessPlatformExtensions"), "UseHeadless", builder, options)!;

        var skia = Assembly.Load("Avalonia.Skia").GetType("Avalonia.SkiaApplicationExtensions")
            ?? throw new MissingMemberException("Avalonia.Skia has no SkiaApplicationExtensions.");
        return (AppBuilder)Call(skia, "UseSkia", builder)!;
    }

    /// <summary>
    /// The headless render timer only moves when told to: tick it at 60 Hz, as a screen would, so layout, rendering and
    /// animations carry on between requests.
    /// </summary>
    public static void StartRenderTicks()
    {
        var platform = TypeOf("Avalonia.Headless.AvaloniaHeadlessPlatform");
        var tick = platform.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "ForceRenderTimerTick");
        if (tick is null)
            return;
        var args = tick.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => tick.Invoke(null, args));
        timer.Start();
    }

    public static void MouseMove(TopLevel topLevel, Point point, RawInputModifiers modifiers)
        => Input("MouseMove", topLevel, point, modifiers);

    public static void MouseDown(TopLevel topLevel, Point point, MouseButton button, RawInputModifiers modifiers)
        => Input("MouseDown", topLevel, point, button, modifiers);

    public static void MouseUp(TopLevel topLevel, Point point, MouseButton button, RawInputModifiers modifiers)
        => Input("MouseUp", topLevel, point, button, modifiers);

    public static void MouseWheel(TopLevel topLevel, Point point, Vector delta, RawInputModifiers modifiers)
        => Input("MouseWheel", topLevel, point, delta, modifiers);

    public static void KeyPress(TopLevel topLevel, Key key, RawInputModifiers modifiers) => Key("KeyPress", topLevel, key, modifiers);

    public static void KeyRelease(TopLevel topLevel, Key key, RawInputModifiers modifiers) => Key("KeyRelease", topLevel, key, modifiers);

    public static void TextInput(TopLevel topLevel, string text) => Input("KeyTextInput", topLevel, text);

    /// <summary>Renders the window now and returns the frame.</summary>
    public static Avalonia.Media.Imaging.Bitmap CaptureRenderedFrame(TopLevel topLevel)
        => (Avalonia.Media.Imaging.Bitmap)(Call(TypeOf("Avalonia.Headless.HeadlessWindowExtensions"), "CaptureRenderedFrame", topLevel)
                                           ?? throw new UiException("The window drew nothing."));

    /// <summary>Key and its physical key (same name, when there is one: keyboard shortcuts match on either).</summary>
    private static void Key(string method, TopLevel topLevel, Key key, RawInputModifiers modifiers)
    {
        var physicalType = typeof(Key).Assembly.GetType("Avalonia.Input.PhysicalKey");
        object? physical = physicalType is not null && Enum.TryParse(physicalType, key.ToString(), out var p) ? p : null;
        Input(method, topLevel, physical is null ? [key, modifiers] : [key, modifiers, physical]);
    }

    private static void Input(string method, TopLevel topLevel, params object[] values)
        => Call(TypeOf("Avalonia.Headless.HeadlessWindowExtensions"), method, [topLevel, .. values]);

    /// <summary>
    /// The public static <paramref name="name"/> on <paramref name="type"/> that takes these values: each value goes to the
    /// first unfilled parameter of its type, the other parameters get their defaults. Of several that fit, the one
    /// that uses the most values.
    /// </summary>
    private static object? Call(Type type, string name, params object[] values)
    {
        object?[]? best = null;
        MethodInfo? bestMethod = null;
        var bestUsed = -1;
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name))
        {
            var parameters = method.GetParameters();
            var args = new object?[parameters.Length];
            var filled = new bool[parameters.Length];
            var used = 0;
            foreach (var value in values)
            {
                var index = -1;
                for (var i = 0; i < parameters.Length && index < 0; i++)
                {
                    if (!filled[i] && parameters[i].ParameterType.IsInstanceOfType(value))
                        index = i;
                }
                if (index < 0)
                    continue;
                args[index] = value;
                filled[index] = true;
                used++;
            }
            var complete = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (filled[i])
                    continue;
                if (parameters[i].HasDefaultValue)
                    args[i] = parameters[i].DefaultValue;
                else if (parameters[i].ParameterType.IsValueType)
                    args[i] = Activator.CreateInstance(parameters[i].ParameterType);
                else if (IsNullable(parameters[i]))
                    args[i] = null;
                else
                    complete = false;
            }
            if (complete && used > bestUsed)
                (best, bestMethod, bestUsed) = (args, method, used);
        }

        if (bestMethod is null)
            throw new MissingMethodException($"This Avalonia.Headless has no usable {type.Name}.{name}.");
        try
        {
            return bestMethod.Invoke(null, best);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static bool IsNullable(ParameterInfo parameter)
        => new NullabilityInfoContext().Create(parameter).WriteState != NullabilityState.NotNull;
}
