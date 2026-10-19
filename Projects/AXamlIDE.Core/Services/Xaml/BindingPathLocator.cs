using System;
using System.Collections.Generic;

namespace AXamlIDE.Services.Xaml;

/// <summary>
/// A binding path under the caret: in <c>ItemsSource="{Binding Run.StartupProjects}"</c> with the caret on
/// "StartupProjects", the path is "Run.StartupProjects" and the segment is 1 ("Run" is 0).
/// </summary>
/// <param name="Path">The whole path, as written.</param>
/// <param name="SegmentIndex">Which dot-separated part the caret is on.</param>
/// <param name="SegmentStart">Where that part starts in the text.</param>
/// <param name="SegmentLength">Its length.</param>
public sealed record BindingPathAtCaret(string Path, int SegmentIndex, int SegmentStart, int SegmentLength);

/// <summary>Finds the binding path (if any) at an offset in AXAML text. Text only; types are resolved elsewhere.</summary>
public static class BindingPathLocator
{
    /// <summary>The markup extensions whose path is a property path of the data context.</summary>
    private static readonly HashSet<string> BindingExtensions = new(StringComparer.Ordinal)
    {
        "Binding", "CompiledBinding", "ReflectionBinding"
    };

    /// <summary>
    /// The binding path at <paramref name="offset"/>: the first positional argument of {Binding ...} (or its
    /// Path=...), when the offset is on it. Null anywhere else (other arguments, other extensions, outside).
    /// </summary>
    public static BindingPathAtCaret? Find(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        if (FindExtensionStart(text, offset) is not { } open)
            return null;

        // {Binding  /  {x:Binding  - the extension's name.
        var nameStart = open + 1;
        while (nameStart < text.Length && text[nameStart] == ' ')
            nameStart++;
        var nameEnd = nameStart;
        while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] is ':' or '_'))
            nameEnd++;
        var name = text[nameStart..nameEnd];
        var colon = name.IndexOf(':');
        if (!BindingExtensions.Contains(colon < 0 ? name : name[(colon + 1)..]))
            return null;

        // Walk the arguments (top-level commas) until the one holding the offset.
        var argumentStart = nameEnd;
        var depth = 0;
        for (var i = nameEnd; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : '}';
            if (c is '{' or '[' or '(')
                depth++;
            else if (c is ']' or ')')
                depth--;
            else if (c == '}' && depth > 0)
                depth--;
            else if ((c == ',' || c == '}' || c is '"' or '\'') && depth == 0)
            {
                if (offset >= argumentStart && offset <= i)
                    return PathIn(text, argumentStart, i, offset, isFirst: argumentStart == nameEnd);
                if (c != ',')
                    return null;
                argumentStart = i + 1;
            }
        }

        return null;
    }

    /// <summary>The path in one argument ("Run.Items" or "Path=Run.Items"), if the offset is on it.</summary>
    private static BindingPathAtCaret? PathIn(string text, int start, int end, int offset, bool isFirst)
    {
        var argument = text[start..end];
        var equals = argument.IndexOf('=');
        int pathStart;
        if (equals < 0)
        {
            if (!isFirst)
                return null;   // only the first positional argument is the path
            pathStart = start;
        }
        else if (argument[..equals].Trim() == "Path")
        {
            pathStart = start + equals + 1;
        }
        else
        {
            return null;
        }

        while (pathStart < end && char.IsWhiteSpace(text[pathStart]))
            pathStart++;
        var pathEnd = end;
        while (pathEnd > pathStart && char.IsWhiteSpace(text[pathEnd - 1]))
            pathEnd--;
        if (offset < pathStart || offset > pathEnd || pathEnd == pathStart)
            return null;

        var path = text[pathStart..pathEnd];
        var relative = offset - pathStart;
        var segmentIndex = 0;
        var segmentStart = 0;
        for (var i = 0; i < relative && i < path.Length; i++)
            if (path[i] == '.')
            {
                segmentIndex++;
                segmentStart = i + 1;
            }
        var segmentEnd = path.IndexOf('.', segmentStart);
        if (segmentEnd < 0)
            segmentEnd = path.Length;

        return new BindingPathAtCaret(path, segmentIndex, pathStart + segmentStart, segmentEnd - segmentStart);
    }

    /// <summary>The '{' of the innermost markup extension around the offset, within the same attribute value.</summary>
    private static int? FindExtensionStart(string text, int offset)
    {
        var depth = 0;
        for (var i = offset - 1; i >= 0; i--)
        {
            switch (text[i])
            {
                case '}':
                    depth++;
                    break;
                case '{' when depth == 0:
                    return i;
                case '{':
                    depth--;
                    break;
                case '"' or '\'' or '<' or '>' or '\n':
                    return null;
            }
        }
        return null;
    }
}
