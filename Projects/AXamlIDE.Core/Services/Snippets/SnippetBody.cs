using System;
using System.Collections.Generic;
using System.Text;

namespace AXamlIDE.Services.Snippets;

/// <summary>A piece of a parsed snippet body.</summary>
public abstract record SnippetPart;

/// <summary>Plain text (may hold line breaks and tabs: the editor indents them like the line they're inserted on).</summary>
public sealed record SnippetText(string Text) : SnippetPart;

/// <summary>
/// A tab stop: $1, ${1:default}, ${1|one,two|} (the first choice). Every occurrence of one index is the same field:
/// the first is typed in, the others follow it.
/// </summary>
public sealed record SnippetField(int Index, string Default) : SnippetPart;

/// <summary>$0: where the caret ends up when the fields are done.</summary>
public sealed record SnippetEnd : SnippetPart;

/// <summary>A variable such as $TM_SELECTED_TEXT or ${TM_FILENAME_BASE:default}, filled in when inserted.</summary>
public sealed record SnippetVariable(string Name, string Default) : SnippetPart;

/// <summary>
/// Parses a snippet body in VS Code's syntax (the LSP snippet grammar): $1, ${1:text}, ${1|a,b|}, $0, $NAME,
/// ${NAME:default}; \$, \} and \\ escape. A placeholder inside a placeholder becomes part of the outer default.
/// </summary>
public static class SnippetBody
{
    /// <param name="variables">Values for variables inside a field's default (${1:${TM_FILENAME_BASE}}); null = unknown.</param>
    public static IReadOnlyList<SnippetPart> Parse(string body, Func<string, string?>? variables = null)
    {
        var parts = new List<SnippetPart>();
        var text = new StringBuilder();
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length && body[i + 1] is '$' or '}' or '\\')
            {
                text.Append(body[i + 1]);
                i += 2;
            }
            else if (c == '$' && TryParseDollar(body, ref i, variables) is { } part)
            {
                Flush();
                parts.Add(part);
            }
            else
            {
                text.Append(c);
                i++;
            }
        }
        Flush();
        return parts;

        void Flush()
        {
            if (text.Length == 0)
                return;
            parts.Add(new SnippetText(text.ToString()));
            text.Clear();
        }
    }

    /// <summary>The body with every field at its default, variables from <paramref name="variables"/> (else their default).</summary>
    public static string Preview(string body, Func<string, string?>? variables = null)
    {
        var preview = new StringBuilder();
        foreach (var part in Parse(body, variables))
            preview.Append(part switch
            {
                SnippetText text => text.Text,
                SnippetField field => field.Default,
                SnippetVariable variable => variables?.Invoke(variable.Name) ?? variable.Default,
                _ => ""
            });
        return preview.ToString();
    }

    /// <summary>At a '$': a tab stop or variable (advancing <paramref name="i"/>), or null to keep it as text.</summary>
    private static SnippetPart? TryParseDollar(string body, ref int i, Func<string, string?>? variables)
    {
        var at = i + 1;
        if (at >= body.Length)
            return null;

        if (char.IsDigit(body[at]))
        {
            var end = at;
            while (end < body.Length && char.IsDigit(body[end]))
                end++;
            i = end;
            return Stop(int.Parse(body.AsSpan(at, end - at)), "");
        }
        if (IsNameStart(body[at]))
        {
            var end = at;
            while (end < body.Length && IsNameChar(body[end]))
                end++;
            i = end;
            return new SnippetVariable(body[at..end], "");
        }
        if (body[at] != '{' || at + 1 >= body.Length)
            return null;

        // ${...}: find the matching '}' (nested placeholders and escapes allowed).
        var close = MatchingBrace(body, at);
        if (close < 0)
            return null;
        var inner = body[(at + 1)..close];
        i = close + 1;

        var nameEnd = 0;
        if (char.IsDigit(inner[0]))
        {
            while (nameEnd < inner.Length && char.IsDigit(inner[nameEnd]))
                nameEnd++;
            var index = int.Parse(inner.AsSpan(0, nameEnd));
            var rest = inner[nameEnd..];
            if (rest.Length == 0)
                return Stop(index, "");
            if (rest[0] == ':')
                return Stop(index, Preview(rest[1..], variables));
            if (rest[0] == '|' && rest.EndsWith('|'))
                return Stop(index, rest[1..^1].Split(',')[0]);
            return Stop(index, "");
        }

        while (nameEnd < inner.Length && IsNameChar(inner[nameEnd]))
            nameEnd++;
        var name = inner[..nameEnd];
        var fallback = nameEnd < inner.Length && inner[nameEnd] == ':' ? Preview(inner[(nameEnd + 1)..], variables) : "";
        return name.Length == 0 ? null : new SnippetVariable(name, fallback);
    }

    private static SnippetPart Stop(int index, string text) => index == 0 ? new SnippetEnd() : new SnippetField(index, text);

    private static int MatchingBrace(string body, int open)
    {
        var depth = 0;
        for (var i = open; i < body.Length; i++)
        {
            if (body[i] == '\\')
                i++;
            else if (body[i] == '{')
                depth++;
            else if (body[i] == '}' && --depth == 0)
                return i;
        }
        return -1;
    }

    private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}

/// <summary>How a file indents, so a snippet's \t matches it (VS Code's "detect indentation", for snippets).</summary>
public static class IndentUnit
{
    /// <summary>"\t" if lines start with tabs; else the smallest step of leading spaces (2-8); else four spaces.</summary>
    public static string Detect(string text)
    {
        var smallest = int.MaxValue;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith('\t'))
                return "\t";
            var spaces = line.Length - line.TrimStart(' ').Length;
            if (spaces >= 2 && spaces < smallest && spaces < line.TrimEnd('\r').Length)   // not a blank line of spaces
                smallest = spaces;
        }
        return new string(' ', smallest is >= 2 and <= 8 ? smallest : 4);
    }
}
