using System;
using System.Collections.Generic;

namespace AXamlIDE.CompletionEngine.Parsing;

/// <summary>
/// Small questions about a style selector's text ("Button.primary:pointerover /template/ ContentPresenter"),
/// answered by scanning characters. <see cref="SelectorParser"/> does the full parse for selector completion.
/// </summary>
public static class SelectorScanner
{
    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '|';

    /// <summary>
    /// The last type the selector names, which is what its setters apply to: "ContentPresenter" in
    /// "Button:pointerover /template/ ContentPresenter#PART". Null when it names none (".primary", "^:pressed").
    /// Uses '|' for a namespace prefix, as selectors do ("local|MyControl").
    /// </summary>
    public static string? LastTypeName(string selector)
    {
        string? last = null;
        var brackets = 0;
        var i = 0;
        while (i < selector.Length)
        {
            var c = selector[i];
            if (c == '[')
                brackets++;
            else if (c == ']')
                brackets--;
            if (!IsIdentifierChar(c))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < selector.Length && IsIdentifierChar(selector[i]))
                i++;
            // A type is an identifier that isn't a class (.x), a name (#x), a pseudo-class or function (:x),
            // "template" (/template/), or anything inside [property=value].
            var before = start > 0 ? selector[start - 1] : ' ';
            if (brackets == 0 && before is not ('.' or '#' or ':' or '/') && char.IsLetter(selector[start]))
                last = selector[start..i];
        }
        return last;
    }

    /// <summary>The style classes the selector uses: "primary" and "big" in "Button.primary.big:pointerover".</summary>
    public static IEnumerable<string> StyleClasses(string selector)
    {
        var brackets = 0;
        for (var i = 0; i < selector.Length; i++)
        {
            var c = selector[i];
            if (c == '[')
                brackets++;
            else if (c == ']')
                brackets--;
            else if (c == '.' && brackets == 0)
            {
                var start = i + 1;
                var end = start;
                while (end < selector.Length && IsIdentifierChar(selector[end]))
                    end++;
                if (end > start)
                    yield return selector[start..end];
                i = end - 1;
            }
        }
    }

    /// <summary>
    /// When the text ends in a style class being typed ("Button.pri", or just "Button."), where that class name
    /// starts. Not inside [...], where a '.' belongs to an attached property ("[(Grid.Row)=1]").
    /// </summary>
    public static bool EndsInStyleClass(ReadOnlySpan<char> selector, out int classStart)
    {
        classStart = selector.Length;
        while (classStart > 0 && IsIdentifierChar(selector[classStart - 1]))
            classStart--;
        if (classStart == 0 || selector[classStart - 1] != '.')
            return false;

        var brackets = 0;
        foreach (var c in selector[..classStart])
        {
            if (c == '[')
                brackets++;
            else if (c == ']')
                brackets--;
        }
        return brackets == 0;
    }
}
