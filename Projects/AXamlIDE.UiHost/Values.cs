using System.Collections;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Media;

namespace AXamlIDE.UiHost;

/// <summary>Reading properties by name (Avalonia properties first, then CLR ones) and writing values short.</summary>
internal static class Values
{
    private const int TextLimit = 80;

    /// <summary>The first of these Avalonia properties the object has, or null (also when it has none of them).</summary>
    public static object? Get(AvaloniaObject target, params string[] names)
    {
        foreach (var name in names)
        {
            if (AvaloniaPropertyRegistry.Instance.FindRegistered(target, name) is { } property)
                return target.GetValue(property);
        }
        return null;
    }

    /// <summary>
    /// A property path from <paramref name="target"/>: "Width", "Grid.Row" (attached), "DataContext.Title" (then CLR
    /// properties). False when the first step isn't there, so asking for Width doesn't print on controls without one.
    /// </summary>
    public static bool TryGetPath(AvaloniaObject target, string path, out object? value)
    {
        value = null;
        var steps = path.Split('.');
        object? current = target;
        if (steps.Length == 2 && Attached(target, steps[0], steps[1]) is { } attached)
        {
            value = target.GetValue(attached);
            return true;
        }

        for (var i = 0; i < steps.Length; i++)
        {
            if (current is null)
            {
                value = null;
                return i > 0;
            }
            if (current is AvaloniaObject avalonia && AvaloniaPropertyRegistry.Instance.FindRegistered(avalonia, steps[i]) is { } property)
                current = avalonia.GetValue(property);
            else if (current.GetType().GetProperty(steps[i], BindingFlags.Public | BindingFlags.Instance) is { GetMethod: not null } clr
                     && clr.GetIndexParameters().Length == 0)
                current = clr.GetValue(current);
            else
                return i > 0 && Fail(out value, $"<no {steps[i]}>");
        }
        value = current;
        return true;
    }

    private static bool Fail(out object? value, string text)
    {
        value = new Raw(text);
        return true;
    }

    /// <summary>A value to print as it is, unquoted.</summary>
    private sealed record Raw(string Text)
    {
        public override string ToString() => Text;
    }

    private static AvaloniaProperty? Attached(AvaloniaObject target, string owner, string name)
        => AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(target.GetType())
            .FirstOrDefault(p => p.Name == name && p.OwnerType.Name == owner);

    public static bool IsPrimitive(object value) => value is string or bool or char or decimal or Enum || value.GetType().IsPrimitive;

    public static string Format(object? value) => value switch
    {
        null => "null",
        Raw raw => raw.Text,
        string text => UiTree.Quote(text.Length > TextLimit ? text[..TextLimit] + "…" : text),
        double number => number.ToString("0.##", CultureInfo.InvariantCulture),
        float number => number.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable formattable when IsPrimitive(value) => formattable.ToString(null, CultureInfo.InvariantCulture),
        ISolidColorBrush brush => brush.Color.ToString() + (brush.Opacity < 1 ? $" @{brush.Opacity:0.##}" : ""),
        IBrush brush => brush.GetType().Name,
        ICollection collection => $"{value.GetType().Name}({collection.Count})",
        IEnumerable and not AvaloniaObject => value.GetType().Name,
        _ => Describe(value)
    };

    /// <summary>Its ToString() when the type has its own (Thickness, Rect, Color, records...), else its type's name.</summary>
    private static string Describe(object value)
    {
        var type = value.GetType();
        var own = type.GetMethod(nameof(ToString), Type.EmptyTypes)?.DeclaringType;
        if (own is null || own == typeof(object) || own == typeof(ValueType))
            return type.Name;
        var text = value.ToString() ?? type.Name;
        return text.Length > TextLimit ? text[..TextLimit] + "…" : text;
    }
}
