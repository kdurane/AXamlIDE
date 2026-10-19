using System.Collections.Generic;

namespace AXamlIDE.Services.Debugging;

/// <summary>The expression a DataTip shows, and where it is in the line (for placing the tip).</summary>
public sealed record DebugExpressionAtPoint(int Start, int Length, string Expression);

/// <summary>
/// Finds what to evaluate for a DataTip, as Visual Studio does: the member-access chain up to the identifier
/// under the mouse. Hovering City in <c>person.Home.City</c> gives person.Home.City, hovering Home gives
/// person.Home; <c>this.</c>, <c>?.</c> and indexers (<c>people[i].Name</c>) are included. Nothing that would call
/// a method (<c>Get().Name</c>, or the method name itself), and no keywords or literals.
/// </summary>
public static class DebugExpressionLocator
{
    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "async", "await", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "get", "goto", "if", "implicit", "in", "init", "int",
        "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
        "override", "params", "private", "protected", "public", "readonly", "record", "ref", "return", "sbyte",
        "sealed", "set", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void",
        "volatile", "while", "yield", "nameof", "when", "where", "with", "and", "or", "not"
    ];

    /// <param name="line">The line's text.</param>
    /// <param name="column">The 0-based column under the mouse.</param>
    public static DebugExpressionAtPoint? Find(string line, int column)
    {
        if (column < 0 || column >= line.Length || !IsIdentifierChar(line[column]))
            return null;

        var end = column;
        while (end < line.Length && IsIdentifierChar(line[end]))
            end++;
        var start = column;
        while (start > 0 && IsIdentifierChar(line[start - 1]))
            start--;

        var word = line[start..end];
        if (char.IsDigit(word[0]) || Keywords.Contains(word.TrimStart('@')))
            return null;
        if (NextNonSpace(line, end) == '(')
            return null;   // a method name: evaluating it would need a call

        // Walk left over ".name", "?.name", "[...]" and "this." / "base.".
        var chainStart = start;
        while (true)
        {
            var at = chainStart;
            if (at >= 1 && line[at - 1] == '.')
                at--;
            else
                break;
            if (at >= 1 && line[at - 1] == '?')
                at--;
            if (at == 0)
                return null;

            var before = line[at - 1];
            if (before == ')')
                return null;   // Get().Name: VS doesn't evaluate calls in DataTips either
            var identifierEnd = at;
            if (before == ']')
            {
                var open = MatchingOpen(line, at - 1);
                if (open < 0)
                    return null;
                identifierEnd = open;
            }

            var identifierStart = identifierEnd;
            while (identifierStart > 0 && IsIdentifierChar(line[identifierStart - 1]))
                identifierStart--;
            if (identifierStart == identifierEnd || char.IsDigit(line[identifierStart]))
                return null;   // "".Length, 1.ToString()...
            if (identifierStart > 0 && line[identifierStart - 1] == ')')
                return null;
            chainStart = identifierStart;
        }

        // An indexer straight after the word under the mouse belongs to the next segment, not this one.
        var text = line[chainStart..end].Replace("?.", ".");
        return new DebugExpressionAtPoint(chainStart, end - chainStart, text);
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    private static char NextNonSpace(string line, int from)
    {
        for (var i = from; i < line.Length; i++)
            if (!char.IsWhiteSpace(line[i]))
                return line[i];
        return '\0';
    }

    /// <summary>The '[' matching the ']' at <paramref name="close"/>, or -1.</summary>
    private static int MatchingOpen(string line, int close)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            if (line[i] == ']') depth++;
            else if (line[i] == '[' && --depth == 0) return i;
            else if (line[i] is '(' or ')' && depth > 0) return -1;   // a call inside the index: don't evaluate it
        }
        return -1;
    }
}
