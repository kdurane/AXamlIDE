using System;
using System.Collections.Generic;

namespace AXamlIDE.CompletionEngine.Parsing;

/// <summary>
/// An attribute in a start tag. <see cref="Value"/> is null while the value isn't finished (no closing quote yet)
/// or the attribute has no value.
/// </summary>
public readonly record struct XamlAttribute(string Name, string? Value, int NameStart);

/// <summary>A start tag: the element name, where its '&lt;' is, and its attributes up to the tag's end.</summary>
public sealed record XamlStartTag(string Name, int Start, IReadOnlyList<XamlAttribute> Attributes)
{
    /// <summary>The first finished value of any of these attributes, in the order given.</summary>
    public string? GetAttribute(IReadOnlyList<string> names)
    {
        foreach (var name in names)
            foreach (var attribute in Attributes)
                if (attribute.Name == name && attribute.Value is not null)
                    return attribute.Value;
        return null;
    }
}

/// <summary>
/// Reads start tags by scanning characters: what the completion engine needs to know about elements other than the
/// one being edited (a parent's x:DataType, every x:Name or x:Key in the document, the attributes already set).
/// </summary>
public static class XamlTagScanner
{
    public static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';

    /// <summary>The element name of the start tag whose '&lt;' is at <paramref name="start"/>; null when there isn't one.</summary>
    public static string? ReadElementName(ReadOnlySpan<char> text, int start)
    {
        if (start < 0 || start >= text.Length || text[start] != '<')
            return null;
        var end = start + 1;
        while (end < text.Length && IsNameChar(text[end]))
            end++;
        return end == start + 1 ? null : text[(start + 1)..end].ToString();
    }

    /// <summary>
    /// The start tag whose '&lt;' is at <paramref name="start"/>, read up to its '&gt;' (or the end of the text).
    /// Null for anything else: a closing tag, a comment, the declaration.
    /// </summary>
    public static XamlStartTag? ReadStartTag(ReadOnlySpan<char> text, int start)
    {
        if (ReadElementName(text, start) is not { } name)
            return null;

        var attributes = new List<XamlAttribute>();
        var i = start + 1 + name.Length;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length || text[i] is '>' or '/' or '<')
                break;

            var nameStart = i;
            while (i < text.Length && IsNameChar(text[i]))
                i++;
            if (i == nameStart)
            {
                i++; // not an attribute name (a stray character): step over it
                continue;
            }
            var attributeName = text[nameStart..i].ToString();

            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            string? value = null;
            if (i < text.Length && text[i] == '=')
            {
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                if (i < text.Length && text[i] is '"' or '\'')
                {
                    var quote = text[i++];
                    var valueStart = i;
                    while (i < text.Length && text[i] != quote)
                        i++;
                    if (i < text.Length)
                    {
                        value = text[valueStart..i].ToString();
                        i++;
                    }
                }
            }
            attributes.Add(new XamlAttribute(attributeName, value, nameStart));
        }
        return new XamlStartTag(name, start, attributes);
    }

    /// <summary>Every start tag in the text, skipping comments and CDATA.</summary>
    public static IEnumerable<XamlStartTag> StartTags(string text)
    {
        var i = 0;
        while ((i = text.IndexOf('<', i)) >= 0)
        {
            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0)
                    yield break;
                i = end + 3;
                continue;
            }
            if (string.CompareOrdinal(text, i, "<![CDATA[", 0, 9) == 0)
            {
                var end = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                if (end < 0)
                    yield break;
                i = end + 3;
                continue;
            }

            if (ReadStartTag(text.AsSpan(), i) is { } tag)
                yield return tag;
            i++;
        }
    }

    /// <summary>Elements with a Name or x:Name, and their tag names.</summary>
    public static IEnumerable<(string Name, string TagName)> NamedElements(string text)
    {
        foreach (var tag in StartTags(text))
            foreach (var attribute in tag.Attributes)
                if (attribute is { Value.Length: > 0 } && IsDirective(attribute.Name, "Name"))
                    yield return (attribute.Value, tag.Name);
    }

    /// <summary>Every x:Key value in the text.</summary>
    public static IEnumerable<string> ResourceKeys(string text)
    {
        foreach (var tag in StartTags(text))
            foreach (var attribute in tag.Attributes)
                if (attribute is { Value.Length: > 0 } && attribute.Name.EndsWith(":Key", StringComparison.Ordinal))
                    yield return attribute.Value;
    }

    /// <summary>Every style class used in a Selector="..." in the text (".primary" in "Button.primary:pointerover").</summary>
    public static IEnumerable<string> StyleClasses(string text)
    {
        foreach (var tag in StartTags(text))
            foreach (var attribute in tag.Attributes)
                if (attribute is { Name: "Selector", Value: { } selector })
                    foreach (var styleClass in SelectorScanner.StyleClasses(selector))
                        yield return styleClass;
    }

    /// <summary>"Name" or "x:Name" (any prefix): XAML lets both name an element.</summary>
    private static bool IsDirective(string attributeName, string directive)
    {
        if (attributeName == directive)
            return true;
        var colon = attributeName.IndexOf(':');
        return colon > 0 && attributeName.AsSpan(colon + 1).SequenceEqual(directive);
    }
}
