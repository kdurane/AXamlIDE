using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AXamlIDE.UiHost;

/// <summary>
/// act: one thing done with the mouse or keyboard (through Avalonia.Headless, so only into the app's own windows),
/// then a short wait, then what changed in the tree: controls added (+), removed (-) and changed (~, with the line it
/// had before). Moves and resizes alone are only counted, since one change can shift half the window.
/// </summary>
internal sealed partial class UiSession
{
    private const int ChangeLimit = 40;

    /// <summary>Where press left the button down, for release.</summary>
    private (TopLevel TopLevel, Point Point, MouseButton Button)? _pressed;

    private async Task<JsonObject> ActAsync(JsonObject request)
    {
        RequireWindow();
        var action = (string?)request["action"] ?? throw new UiException("No action given.");
        var before = _tree.Lines(_windows, new UiTree.Options());

        string done;
        try
        {
            done = Act(action, request);
        }
        catch (UiException)
        {
            throw;   // a bad selector or argument: nothing was done
        }
        catch (Exception ex)
        {
            done = $"{action}: the app threw {Program.Describe(ex)}";
        }

        await SettleAsync((int?)Number(request["ms"]) ?? (action == "wait" ? 500 : 200));
        return Text(done + "\n" + Changes(before, _tree.Lines(_windows, new UiTree.Options())));
    }

    private string Act(string action, JsonObject request)
    {
        var modifiers = Modifiers((string?)request["modifiers"]);
        var target = (string?)request["target"] is { Length: > 0 } selector ? Resolve(selector) : null;
        switch (action)
        {
            case "click":
            case "double_click":
            case "right_click":
            {
                var (topLevel, point, notes) = PointOf(target, request);
                var button = action == "right_click" ? MouseButton.Right : MouseButton.Left;
                Headless.MouseMove(topLevel, point, modifiers);
                for (var i = 0; i < (action == "double_click" ? 2 : 1); i++)
                {
                    Headless.MouseDown(topLevel, point, button, modifiers);
                    Headless.MouseUp(topLevel, point, button, modifiers);
                }
                return $"{Past(action)} {Where(target, point)}.{notes}";
            }
            case "hover":
            {
                var (topLevel, point, notes) = PointOf(target, request);
                Headless.MouseMove(topLevel, point, modifiers);
                return $"Moved the mouse to {Where(target, point)}.{notes}";
            }
            case "press":
            {
                var (topLevel, point, notes) = PointOf(target, request);
                Headless.MouseMove(topLevel, point, modifiers);
                Headless.MouseDown(topLevel, point, MouseButton.Left, modifiers);
                _pressed = (topLevel, point, MouseButton.Left);
                return $"Pressed (and held) the left button at {Where(target, point)}.{notes}";
            }
            case "release":
            {
                var (topLevel, point, button) = _pressed ?? (TopWindow, default, MouseButton.Left);
                if (target is not null || request["x"] is not null)
                    (topLevel, point, _) = PointOf(target, request);
                Headless.MouseMove(topLevel, point, modifiers);
                Headless.MouseUp(topLevel, point, button, modifiers);
                _pressed = null;
                return $"Released the button at {Where(target, point)}.";
            }
            case "drag":
            {
                var (topLevel, from, notes) = PointOf(target, request);
                var to = (string?)request["to"] is { Length: > 0 } toSelector
                    ? Center(Resolve(toSelector), topLevel)
                    : from + new Point(Number(request["dx"]) ?? 0, Number(request["dy"]) ?? 0);
                Headless.MouseMove(topLevel, from, modifiers);
                Headless.MouseDown(topLevel, from, MouseButton.Left, modifiers);
                const int steps = 8;
                for (var i = 1; i <= steps; i++)
                    Headless.MouseMove(topLevel, from + (to - from) * i / steps, modifiers | RawInputModifiers.LeftMouseButton);
                Headless.MouseUp(topLevel, to, MouseButton.Left, modifiers);
                return $"Dragged from {Where(target, from)} to {Point(to)}.{notes}";
            }
            case "scroll":
            {
                var (topLevel, point, notes) = PointOf(target, request);
                var (dx, dy) = (Number(request["dx"]) ?? 0, Number(request["dy"]) ?? 3);
                Headless.MouseMove(topLevel, point, modifiers);
                Headless.MouseWheel(topLevel, point, new Vector(-dx, -dy), modifiers);
                return Invariant($"Scrolled {dy:0.#} down, {dx:0.#} right at {Where(target, point)}.{notes}");
            }
            case "focus":
            {
                var control = target ?? throw new UiException("focus needs a target.");
                return control.Focus() ? $"Focused {_tree.Short(control)}." : $"{_tree.Short(control)} didn't take focus (not focusable, hidden or disabled?).";
            }
            case "type":
            {
                var text = (string?)request["text"] ?? throw new UiException("type needs text.");
                if (target is not null && !target.Focus())
                    return $"{_tree.Short(target)} didn't take focus, so nothing was typed.";
                var topLevel = target is not null ? TopLevelOf(target) : TopWindow;
                Headless.TextInput(topLevel, text);
                return $"Typed {UiTree.Quote(text)} into {Focused(topLevel)}.";
            }
            case "key":
            {
                var keys = (string?)request["keys"] ?? throw new UiException("key needs keys, e.g. \"Ctrl+S\" or \"Tab Tab Enter\".");
                if (target is not null && !target.Focus())
                    return $"{_tree.Short(target)} didn't take focus, so no keys were pressed.";
                var topLevel = target is not null ? TopLevelOf(target) : TopWindow;
                var to = Focused(topLevel);
                foreach (var combo in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var (key, comboModifiers) = ParseKey(combo);
                    Headless.KeyPress(topLevel, key, comboModifiers | modifiers);
                    Headless.KeyRelease(topLevel, key, comboModifiers | modifiers);
                }
                return $"Pressed {keys} in {to}.";
            }
            case "wait":
                return "Waited.";
            default:
                throw new UiException($"Unknown action {action}.");
        }
    }

    private Window TopWindow => _windows[^1];

    private static TopLevel TopLevelOf(Visual visual)
        => TopLevel.GetTopLevel(visual) ?? throw new UiException("That control isn't in a window.");

    private string Focused(TopLevel topLevel)
        => topLevel.FocusManager?.GetFocusedElement() is Visual focused ? _tree.Short(focused) : "nothing focused (the window)";

    /// <summary>
    /// The point to act at, in its window: the target's centre (or x, y inside the target), or x, y in the newest
    /// window. Notes what the model may not expect: the target being disabled, or covered by something else there.
    /// </summary>
    private (TopLevel TopLevel, Point Point, string Notes) PointOf(Control? target, JsonObject request)
    {
        var (x, y) = (Number(request["x"]), Number(request["y"]));
        if (target is null)
        {
            if (x is null || y is null)
                throw new UiException("Give a target, or x and y (DIPs in the newest window).");
            return (TopWindow, new Point(x.Value, y.Value), "");
        }

        var topLevel = TopLevelOf(target);
        var point = x is not null && y is not null && target.TranslatePoint(new Point(x.Value, y.Value), topLevel) is { } inside
            ? inside
            : Center(target, topLevel);

        var notes = new StringBuilder();
        if (!target.IsEffectivelyVisible || target.Bounds.Width < 1 || target.Bounds.Height < 1)
            notes.Append($" Note: {_tree.Short(target)} isn't visible or has no size.");
        else if (!new Rect(topLevel.Bounds.Size).Contains(point))
            notes.Append($" Note: {_tree.Short(target)} is outside the window (a bigger one: ui_open with width/height), so it got nothing.");
        else if (!target.IsEffectivelyEnabled)
            notes.Append($" Note: {_tree.Short(target)} is disabled.");
        else if (topLevel.InputHitTest(point) is Visual hit && hit != target && !target.IsVisualAncestorOf(hit))
            notes.Append($" Note: {_tree.Short(hit)} is on top at that point, so it got the input.");
        return (topLevel, point, notes.ToString());
    }

    private static Point Center(Visual visual, TopLevel topLevel)
        => visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), topLevel)
           ?? throw new UiException("That control isn't in the same window.");

    private string Where(Control? target, Point point) => target is null ? Point(point) : $"{_tree.Short(target)} ({Point(point)})";

    private static string Point(Point point) => Invariant($"{point.X:0},{point.Y:0}");

    private static string Past(string action) => action switch
    {
        "double_click" => "Double-clicked",
        "right_click" => "Right-clicked",
        _ => "Clicked"
    };

    private static RawInputModifiers Modifiers(string? text)
    {
        var modifiers = RawInputModifiers.None;
        foreach (var name in (text ?? "").Split([',', '+', ' '], StringSplitOptions.RemoveEmptyEntries))
            modifiers |= Modifier(name) ?? throw new UiException($"Unknown modifier {name} (Ctrl, Shift, Alt, Meta).");
        return modifiers;
    }

    private static RawInputModifiers? Modifier(string name) => name.ToLowerInvariant() switch
    {
        "ctrl" or "control" => RawInputModifiers.Control,
        "shift" => RawInputModifiers.Shift,
        "alt" or "option" => RawInputModifiers.Alt,
        "meta" or "cmd" or "win" or "super" => RawInputModifiers.Meta,
        _ => null
    };

    /// <summary>"Ctrl+Shift+S" → (S, Control | Shift). Keys by Avalonia's Key names, plus Esc, Del, Ins, digits and the like.</summary>
    private static (Key Key, RawInputModifiers Modifiers) ParseKey(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new UiException($"No key in {combo}.");
        var modifiers = RawInputModifiers.None;
        foreach (var part in parts[..^1])
            modifiers |= Modifier(part) ?? throw new UiException($"Unknown modifier {part} in {combo}.");

        var name = parts[^1];
        name = name.ToLowerInvariant() switch
        {
            "esc" => "Escape",
            "del" => "Delete",
            "ins" => "Insert",
            "return" => "Enter",
            "backspace" => "Back",
            "pgup" => "PageUp",
            "pgdn" => "PageDown",
            "space" => "Space",
            _ when name.Length == 1 && char.IsDigit(name[0]) => "D" + name,
            _ => name
        };
        return Enum.TryParse<Key>(name, ignoreCase: true, out var key) && !int.TryParse(name, out _)
            ? (key, modifiers)
            : throw new UiException($"Unknown key {parts[^1]} (Avalonia Key names: Enter, Tab, Escape, Left, F5, A...).");
    }

    // ------------------------------------------------------------ what changed

    /// <summary>"was X": what <paramref name="was"/> had where it differs from <paramref name="now"/> (the words between their common start and end); "new: X" when it had nothing there.</summary>
    private static string Difference(string was, string now)
    {
        var old = was.Split(' ');
        var current = now.Split(' ');
        var start = 0;
        while (start < old.Length && start < current.Length && old[start] == current[start])
            start++;
        var end = 0;
        while (end < old.Length - start && end < current.Length - start && old[^(end + 1)] == current[^(end + 1)])
            end++;
        var middle = string.Join(' ', old[start..^end]);
        return middle.Length > 0 ? "was " + middle : "new: " + string.Join(' ', current[start..^end]);
    }

    private string Changes(List<UiTree.Line> before, List<UiTree.Line> after)
    {
        var old = before.ToDictionary(l => l.Id);
        var now = after.Select(l => l.Id).ToHashSet();
        var text = new StringBuilder();
        var shown = 0;
        var hidden = 0;
        var moved = 0;

        void Add(string line)
        {
            if (shown++ < ChangeLimit)
                text.AppendLine(line);
            else
                hidden++;
        }

        var addedDepth = int.MaxValue;
        foreach (var line in after)
        {
            if (!old.TryGetValue(line.Id, out var was))
            {
                addedDepth = Math.Min(addedDepth, line.Depth);
                Add("+ " + new string(' ', 2 * (line.Depth - addedDepth)) + line.Text);
                continue;
            }
            addedDepth = int.MaxValue;
            if (was.Key != line.Key)
                Add("~ " + line.Text + "   (" + Difference(was.Key, line.Key) + ")");
            else if (was.Geometry != line.Geometry)
                moved++;
        }
        var removedDepth = int.MaxValue;   // inside a removed control: its children go without saying
        foreach (var line in before)
        {
            if (line.Depth <= removedDepth)
                removedDepth = int.MaxValue;
            if (now.Contains(line.Id) || removedDepth != int.MaxValue)
                continue;
            Add("- " + _tree.Short(line.Visual));
            removedDepth = line.Depth;
        }

        if (hidden > 0)
            text.AppendLine($"… {hidden} more changes (ui_tree shows it all).");
        if (moved > 0)
            text.AppendLine($"{moved} other controls moved or resized.");
        return shown == 0 && moved == 0 ? "Nothing changed in the tree." : text.ToString().TrimEnd();
    }
}
