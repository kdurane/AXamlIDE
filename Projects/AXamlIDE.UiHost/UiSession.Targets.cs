using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace AXamlIDE.UiHost;

/// <summary>
/// Pointing at a control:
///   @7               the id from a tree (best: never ambiguous)
///   #SaveButton      x:Name
///   Button           the type (an exact name first, else a base type's)
///   Button "Save"    the type and the text it shows
///   "Save"           any control showing that text (exactly, else ignoring case, else containing it);
///                    unquoted too, when no type has that name
/// Several matches is an error listing them (with their ids), unless just one of them is visible.
/// </summary>
internal sealed partial class UiSession
{
    private Control Resolve(string selector)
    {
        selector = selector.Trim();
        if (selector.StartsWith('@'))
        {
            if (int.TryParse(selector[1..], out var id) && _tree.Find(id) is Control byId)
                return InWindow(byId) ? byId : throw new UiException($"{_tree.Short(byId)} isn't in an open window any more.");
            throw new UiException($"No control {selector} (ids come from ui_open, ui_tree and ui_act).");
        }

        var (typeName, text) = Parse(selector);
        var all = _windows.SelectMany(w => w.GetSelfAndVisualDescendants()).OfType<Control>().ToList();
        List<Control> matches;
        if (typeName is not null && typeName.StartsWith('#'))
            matches = all.Where(c => c.Name == typeName[1..]).ToList();
        else
        {
            var candidates = typeName is null ? all : OfType(all, typeName);
            matches = text is null ? candidates : ByText(candidates, text);
            if (matches.Count == 0 && text is null && typeName is not null)
                matches = ByText(all, typeName);   // Save, unquoted: no type of that name, so the text
        }

        if (matches.Count > 1 && matches.Count(c => c.IsEffectivelyVisible) == 1)
            matches = matches.Where(c => c.IsEffectivelyVisible).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new UiException($"No control matches {selector}."),
            _ => throw new UiException($"{matches.Count} controls match {selector}; use an id: " +
                                                     string.Join(", ", matches.Take(8).Select(_tree.Short)) + (matches.Count > 8 ? ", …" : ""))
        };
    }

    private bool InWindow(Visual visual) => TopLevel.GetTopLevel(visual) is Window window && _windows.Contains(window);

    /// <summary>'Button "Save"' → (Button, Save); '"Save"' → (null, Save); '#Save' → (#Save, null).</summary>
    private static (string? Type, string? Text) Parse(string selector)
    {
        var quote = selector.IndexOf('"');
        if (quote < 0)
            return (selector, null);
        var end = selector.LastIndexOf('"');
        var text = end > quote ? selector[(quote + 1)..end] : selector[(quote + 1)..];
        var type = selector[..quote].Trim();
        return (type.Length > 0 ? type : null, text);
    }

    private static List<Control> OfType(List<Control> controls, string typeName)
    {
        var exact = controls.Where(c => c.GetType().Name == typeName).ToList();
        if (exact.Count > 0)
            return exact;
        return controls.Where(c => BaseTypes(c.GetType()).Any(t => t.Name == typeName)).ToList();

        static IEnumerable<Type> BaseTypes(Type? type)
        {
            for (; type is not null; type = type.BaseType)
                yield return type;
        }
    }

    /// <summary>Controls showing this text, outermost only (a Button "Save", not also the TextBlock inside it).</summary>
    private static List<Control> ByText(List<Control> controls, string text)
    {
        foreach (var match in (Func<string, bool>[])
                 [
                     shown => shown == text,
                     shown => string.Equals(shown, text, StringComparison.OrdinalIgnoreCase),
                     shown => shown.Contains(text, StringComparison.OrdinalIgnoreCase)
                 ])
        {
            var found = controls.Where(c => UiTree.TextsOf(c).Any(match)).ToList();
            found = found.Where(c => !found.Any(other => other != c && other.IsVisualAncestorOf(c))).ToList();
            if (found.Count > 0)
                return found;
        }
        return [];
    }
}
