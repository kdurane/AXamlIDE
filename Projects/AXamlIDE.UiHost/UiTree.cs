using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AXamlIDE.UiHost;

/// <summary>
/// The control tree as text, one line per control, for the model to read:
///   @7 Button #SaveButton "Save" 12,40 80x32 [focus pointerover] .accent
/// @7 is the control's id (kept for the session, so a later call can point at it), then its type, x:Name, text, its
/// place in its window (DIPs) and its states (the pseudo-classes that say something). By default controls a template
/// made are left out (a Button's border and presenter), as is a TextBlock just repeating its parent's text, so what's
/// left reads like the XAML; <see cref="Options.All"/> keeps the whole visual tree.
/// </summary>
internal sealed class UiTree
{
    /// <summary>Pseudo-classes worth showing. :disabled shows only where it starts; :pointerover only on the deepest control shown.</summary>
    private static readonly string[] States =
        [":focus", ":pressed", ":checked", ":indeterminate", ":selected", ":expanded", ":open", ":dropdownopen", ":error"];

    private const int TextLimit = 60;

    private readonly ConditionalWeakTable<Visual, StrongBox<int>> _ids = new();
    private readonly Dictionary<int, WeakReference<Visual>> _byId = [];
    private int _next;

    /// <param name="Layout">Each control's position in its window and size (DIPs); without, only [offscreen] and [nosize] say where.</param>
    public sealed record Options(bool All = false, int MaxDepth = int.MaxValue, IReadOnlyList<string>? Props = null, bool Layout = false);

    /// <param name="Key">The line without position, size and pointer-over, which change all the time: what a real change changes.</param>
    /// <param name="Geometry">The position and size.</param>
    public sealed record Line(int Id, int Depth, string Text, string Key, string Geometry, Visual Visual);

    public int IdOf(Visual visual)
    {
        if (_ids.TryGetValue(visual, out var box))
            return box.Value;
        var id = ++_next;
        _ids.Add(visual, new StrongBox<int>(id));
        _byId[id] = new WeakReference<Visual>(visual);
        return id;
    }

    public Visual? Find(int id) => _byId.TryGetValue(id, out var weak) && weak.TryGetTarget(out var visual) ? visual : null;

    /// <summary>"@7 Button "Save"": enough to tell the model which control was meant.</summary>
    public string Short(Visual visual)
    {
        var text = new StringBuilder($"@{IdOf(visual)} {visual.GetType().Name}");
        if (visual is Control { Name: { Length: > 0 } name })
            text.Append(" #").Append(name);
        if (TextsOf(visual).FirstOrDefault() is { } shown)
            text.Append(' ').Append(Quote(shown));
        return text.ToString();
    }

    /// <summary>The text a control shows: its own (Text, Content...), else what's inside it when that's just text and icons, else its tooltip.</summary>
    public static IReadOnlyList<string> TextsOf(Visual visual)
        => TextOf(visual) is { } own ? [own] : FoldedTexts(visual) is { Count: > 0 } folded ? folded : TipOf(visual) is { } tip ? [tip] : [];

    public List<Line> Lines(IEnumerable<Visual> roots, Options options)
    {
        var lines = new List<Line>();
        foreach (var root in roots)
            Walk(root, 0, null, false, lines, options);
        return lines;
    }

    /// <summary>Adds the lines for <paramref name="visual"/>'s subtree; true when one of them took the pointer-over mark.</summary>
    private bool Walk(Visual visual, int depth, string? parentText, bool parentDisabled, List<Line> lines, Options options)
    {
        if (IsNoise(visual) || (!visual.IsVisible && !options.All))
            return false;

        var text = TextOf(visual);
        var disabled = visual is InputElement { IsEffectivelyEnabled: false };
        if (!options.All && (IsLayer(visual) || !IsShown(visual, parentText, text)))
            return WalkChildren(visual, depth, parentText, parentDisabled, lines, options);
        if (depth > options.MaxDepth)
            return false;

        var index = lines.Count;
        lines.Add(null!);   // filled in below, once the children say whether the pointer-over mark is theirs
        var folded = options.All || text is not null ? null : FoldedTexts(visual);
        var childHover = false;
        if (folded is null && visual.IsVisible && depth < options.MaxDepth)
            childHover = WalkChildren(visual, depth + 1, text ?? parentText, disabled, lines, options);

        var hover = !childHover && visual is InputElement { IsPointerOver: true };
        var more = folded is null && visual.IsVisible && depth == options.MaxDepth && HasShownChildren(visual, depth, text ?? parentText, options);
        var (line, key, geometry) = Describe(visual, text, folded, disabled && !parentDisabled, hover, more, options);
        lines[index] = new Line(IdOf(visual), depth, line, key, geometry, visual);
        return hover || childHover;
    }

    /// <summary>Whether a deeper tree would show something under <paramref name="visual"/> (for the … at the depth limit).</summary>
    private bool HasShownChildren(Visual visual, int depth, string? text, Options options)
    {
        var probe = new List<Line>();
        WalkChildren(visual, depth + 1, text, false, probe, options with { MaxDepth = depth + 1, Props = null });
        return probe.Count > 0;
    }

    private bool WalkChildren(Visual visual, int depth, string? parentText, bool parentDisabled, List<Line> lines, Options options)
    {
        var any = false;
        foreach (var child in visual.GetVisualChildren())
            any |= Walk(child, depth, parentText, parentDisabled, lines, options);
        return any;
    }

    /// <summary>Layers of the window that are the framework's, not the app's: focus rectangles, a TextBox's selection handles.</summary>
    private static bool IsNoise(Visual visual) => visual.GetType().Name is "AdornerLayer" or "TextSelectorLayer";

    /// <summary>
    /// The window's layers and popup hosts: left out, but what's in them (an open menu, a tooltip) is shown. By name,
    /// since which of them are public changes between Avalonia versions.
    /// </summary>
    private static bool IsLayer(Visual visual)
        => visual.GetType().Name is "OverlayLayer" or "LightDismissOverlayLayer" or "VisualLayerManager" or "OverlayPopupHost" or "ChromeOverlayLayer";

    /// <summary>
    /// Left out (its children take its place): what a template made, a TextBlock repeating its parent's text, and a
    /// layout wrapper (an unnamed panel or border around just one thing).
    /// </summary>
    private static bool IsShown(Visual visual, string? parentText, string? text)
    {
        if (visual is StyledElement { TemplatedParent: not null })
            return false;
        if (visual is TextBlock && text is not null && text == parentText)
            return false;
        return !(IsPassive(visual) && !IsLeaf(visual) && text is null && visual is Control { Name: null or "" }
                 && visual.GetVisualChildren().Count(c => c.IsVisible) == 1);
    }

    /// <summary>Passive controls whose insides are only how they're drawn.</summary>
    private static bool IsLeaf(Visual visual) => visual is IconElement or Image or Shape or Viewbox or TextBlock;

    /// <summary>Controls that only lay out or show something: nothing to click or type into.</summary>
    private static bool IsPassive(Visual visual)
        => visual is Panel or Decorator or ContentPresenter or Viewbox or Image or Shape or IconElement
           || (visual is TextBlock && visual.GetType().Name != "SelectableTextBlock")
           || !visual.GetType().IsVisible;   // the framework's own internals, such as a Viewbox's container

    private const int FoldLimit = 12;

    /// <summary>
    /// When everything inside <paramref name="visual"/> is passive (a button's icon and label, a list row of
    /// TextBlocks), the texts in it, to go on its own line instead of a line each; else null. Not for a subtree
    /// with a named control in it (named to be found) or more than a dozen texts and icons (that's a layout worth seeing).
    /// </summary>
    private static List<string>? FoldedTexts(Visual visual)
    {
        var texts = new List<string>();
        var count = 0;
        var stack = new Stack<Visual>(visual.GetVisualChildren().Reverse());
        if (stack.Count == 0)
            return null;
        while (stack.Count > 0)
        {
            var inside = stack.Pop();
            if (!inside.IsVisible || IsNoise(inside))
                continue;
            if (!IsPassive(inside) || inside is Control { Name.Length: > 0 } && inside is StyledElement { TemplatedParent: null })
                return null;
            if (IsLeaf(inside) && ++count > FoldLimit)
                return null;
            if (inside is TextBlock && TextOf(inside) is { } text && !texts.Contains(text))
                texts.Add(text);
            if (IsLeaf(inside))
                continue;
            foreach (var child in inside.GetVisualChildren().Reverse())
                stack.Push(child);
        }
        return texts;
    }

    private (string Line, string Key, string Geometry) Describe(Visual visual, string? text, List<string>? folded, bool disabled, bool hover, bool more,
        Options options)
    {
        var line = new StringBuilder($"@{IdOf(visual)} {visual.GetType().Name}");
        if (visual is Control { Name: { Length: > 0 } name })
            line.Append(" #").Append(name);
        foreach (var shown in (text is null ? folded ?? [] : [text]).Take(6))
            line.Append(' ').Append(Quote(shown));
        if (folded is { Count: > 6 })
            line.Append(" +").Append(folded.Count - 6);
        if (text is null && folded is not { Count: > 0 } && TipOf(visual) is { } tip)
            line.Append(" tip ").Append(Quote(tip));   // an icon button's only words
        if (text is null && visual is TextBox && Values.Get(visual, "PlaceholderText", "Watermark") is string { Length: > 0 } placeholder)
            line.Append(" placeholder ").Append(Quote(placeholder));
        if (Values.Get(visual, "Value") is { } value && Values.IsPrimitive(value))
            line.Append(" value=").Append(Values.Format(value));
        if (Values.Get(visual, "SelectedIndex") is int selected and >= 0)
            line.Append(" selected=").Append(selected);

        var geometry = new StringBuilder();
        var offscreen = false;
        if (TopLevel.GetTopLevel(visual) is Visual root && visual != root && visual.TranslatePoint(default, root) is { } at)
        {
            geometry.Append(CultureInfo.InvariantCulture, $" {Math.Round(at.X)},{Math.Round(at.Y)}");
            offscreen = !new Rect(at, visual.Bounds.Size).Intersects(new Rect(root.Bounds.Size));
        }
        geometry.Append(CultureInfo.InvariantCulture, $" {Math.Round(visual.Bounds.Width)}x{Math.Round(visual.Bounds.Height)}");
        var head = line.Length;

        var states = new List<string>();
        if (!visual.IsVisible)
            states.Add("hidden");
        if (offscreen)
            states.Add("offscreen");   // outside its window: a user couldn't see it without a bigger window
        if (visual.IsVisible && (visual.Bounds.Width < 1 || visual.Bounds.Height < 1))
            states.Add("nosize");
        if (disabled)
            states.Add("disabled");
        if (visual is StyledElement styled)
        {
            states.AddRange(States.Where(styled.Classes.Contains).Select(s => s[1..]));
            if (hover)
                states.Add("pointerover");
        }
        if (states.Count > 0)
            line.Append(" [").Append(string.Join(' ', states)).Append(']');
        if (visual is StyledElement { Classes: var classes })
        {
            foreach (var className in classes.Where(c => !c.StartsWith(':')))
                line.Append(" .").Append(className);
        }

        foreach (var prop in options.Props ?? [])
        {
            if (Values.TryGetPath(visual, prop, out var found))
                line.Append(' ').Append(prop).Append('=').Append(Values.Format(found));
        }
        if (more)
            line.Append(" …");
        var key = line.ToString().Replace(" pointerover]", "]").Replace(" [pointerover]", "").Replace("[pointerover ", "[");
        return (options.Layout ? line.Insert(head, geometry).ToString() : key, key, geometry.ToString());
    }

    /// <summary>Its tooltip or automation name, when that's text.</summary>
    public static string? TipOf(Visual visual)
        => visual is Control control
           && (ToolTip.GetTip(control) as string ?? Avalonia.Automation.AutomationProperties.GetName(control)) is { Length: > 0 } tip
            ? tip
            : null;

    /// <summary>What a control shows as text: its Text, string Content, Header or Title.</summary>
    public static string? TextOf(Visual visual)
    {
        foreach (var name in (string[])["Text", "Content", "Header", "Title"])
        {
            if (Values.Get(visual, name) is { } value && Values.IsPrimitive(value))
            {
                var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
        }
        return null;
    }

    public static string Quote(string text)
    {
        text = text.Replace("\r", "").Replace("\n", "\\n");
        if (text.Length > TextLimit)
            text = text[..TextLimit] + "…";
        return "\"" + text + "\"";
    }
}
