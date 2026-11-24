using System.Collections.Generic;
using System.Linq;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace AXamlIDE.Editor.Folding;

internal static class CSharpFoldingStrategy
{
    public static IEnumerable<NewFolding> CreateFoldings(TextDocument document)
    {
        var text = document.Text;
        var openings = new Stack<int>();
        var regions = new Stack<(int Start, string Name)>();
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
                case '#' when IsFirstOnLine(text, offset):
                    offset = ReadDirective(document, text, offset, regions, foldings);
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

    /// <summary>
    /// Read the preprocessor directive starting at <paramref name="hash"/> and return the offset of its
    /// last character. The whole line is skipped so a quote or brace in a region's name can't throw
    /// the scanner off. A <c>#region</c> folds to its matching <c>#endregion</c> and, as in Visual Studio,
    /// shows the region's name when collapsed.
    /// </summary>
    private static int ReadDirective(TextDocument document, string text, int hash,
        Stack<(int Start, string Name)> regions, List<NewFolding> foldings)
    {
        var lineEnd = document.GetLineByOffset(hash).EndOffset;
        var directive = text[(hash + 1)..lineEnd].TrimStart();

        if (IsDirective(directive, "region"))
        {
            var name = directive["region".Length..].Trim();
            regions.Push((hash, name.Length > 0 ? name : "#region"));
        }
        else if (IsDirective(directive, "endregion") && regions.TryPop(out var region)
                 && document.GetLineByOffset(region.Start).LineNumber != document.GetLineByOffset(hash).LineNumber)
        {
            foldings.Add(new NewFolding(region.Start, lineEnd) { Name = region.Name });
        }

        return lineEnd - 1;
    }

    private static bool IsDirective(string directive, string keyword)
        => directive.StartsWith(keyword, System.StringComparison.Ordinal)
           && (directive.Length == keyword.Length || char.IsWhiteSpace(directive[keyword.Length]));

    private static bool IsFirstOnLine(string text, int offset)
    {
        for (var i = offset - 1; i >= 0 && text[i] is not '\r' and not '\n'; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }

        return true;
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
