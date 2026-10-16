using System.Collections.Generic;
using System.Linq;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace AXamlIDE.Services;

internal static class CSharpFoldingStrategy
{
    public static IEnumerable<NewFolding> CreateFoldings(TextDocument document)
    {
        var text = document.Text;
        var openings = new Stack<int>();
        var foldings = CreateUsingFoldings(document).ToList();
        var inBlockComment = false;

        for (var offset = 0; offset < text.Length; offset++)
        {
            var current = text[offset];

            if (inBlockComment)
            {
                if (current == '*' && At(text, offset + 1, '/'))
                {
                    inBlockComment = false;
                    offset++;
                }

                continue;
            }

            switch (current)
            {
                case '/' when At(text, offset + 1, '/'):
                    {
                        while (offset < text.Length && text[offset] is not '\r' and not '\n')
                            offset++;
                        continue;
                    }
                case '/' when At(text, offset + 1, '*'):
                    inBlockComment = true;
                    offset++;
                    continue;
                case '"' or '@' or '$' when TrySkipString(text, offset, out var end):
                    offset = end;
                    continue;
                case '\'':
                    offset = SkipCharacter(text, offset);
                    continue;
                case '{':
                    openings.Push(offset);
                    break;
                case '}' when openings.TryPop(out var opening)
                              && document.GetLineByOffset(opening).LineNumber
                              != document.GetLineByOffset(offset).LineNumber:
                    foldings.Add(new NewFolding(opening, offset + 1));
                    break;
            }
        }

        return foldings.OrderBy(folding => folding.StartOffset);
    }

    private static IEnumerable<NewFolding> CreateUsingFoldings(TextDocument document)
    {
        var firstUsing = -1;
        var lastUsingEnd = -1;
        var usingCount = 0;

        foreach (var line in document.Lines)
        {
            var content = document.GetText(line);
            var trimmed = content.Trim();

            if (trimmed.Length == 0 || IsCommentLine(trimmed))
                continue;

            if (!IsUsingDirective(trimmed))
                break;

            if (firstUsing < 0)
                firstUsing = line.Offset;
            lastUsingEnd = line.EndOffset;
            usingCount++;
        }

        if (usingCount < 2)
            yield break;

        yield return new NewFolding(firstUsing, lastUsingEnd)
        {
            Name = "usings",
            DefaultClosed = true
        };
    }

    private static bool IsUsingDirective(string line)
    {
        var directive = line.StartsWith("global using ", System.StringComparison.Ordinal)
            ? line["global ".Length..]
            : line;
        if (!directive.StartsWith("using ", System.StringComparison.Ordinal)
            || !directive.EndsWith(';'))
            return false;

        var target = directive["using ".Length..].TrimStart();
        return !target.StartsWith("var ", System.StringComparison.Ordinal)
               && !target.StartsWith('(');
    }

    private static bool IsCommentLine(string line)
        => line.StartsWith("//", System.StringComparison.Ordinal)
           || line.StartsWith("/*", System.StringComparison.Ordinal)
           || line.StartsWith('*')
           || line.StartsWith("*/", System.StringComparison.Ordinal);

    private static bool TrySkipString(string text, int start, out int end)
    {
        var quote = start;
        var hasVerbatimPrefix = false;
        while (quote < text.Length && text[quote] is '@' or '$')
        {
            hasVerbatimPrefix |= text[quote] == '@';
            quote++;
        }

        if (quote >= text.Length || text[quote] != '"')
        {
            end = start;
            return false;
        }

        var quoteCount = 1;
        while (quote + quoteCount < text.Length && text[quote + quoteCount] == '"')
            quoteCount++;

        if (!hasVerbatimPrefix && quoteCount >= 3)
        {
            for (var i = quote + quoteCount; i < text.Length; i++)
            {
                if (text[i] != '"') continue;

                var closingQuotes = 1;
                while (i + closingQuotes < text.Length && text[i + closingQuotes] == '"')
                    closingQuotes++;

                if (closingQuotes >= quoteCount)
                {
                    end = i + quoteCount - 1;
                    return true;
                }

                i += closingQuotes - 1;
            }

            end = text.Length - 1;
            return true;
        }

        for (var i = quote + 1; i < text.Length; i++)
        {
            if (!hasVerbatimPrefix && text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] != '"') continue;
            if (hasVerbatimPrefix && At(text, i + 1, '"'))
            {
                i++;
                continue;
            }

            end = i;
            return true;
        }

        end = text.Length - 1;
        return true;
    }

    private static int SkipCharacter(string text, int start)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\\':
                    i++;
                    continue;
                case '\'':
                    return i;
            }
        }

        return text.Length - 1;
    }

    private static bool At(string text, int offset, char expected)
        => offset < text.Length && text[offset] == expected;
}
