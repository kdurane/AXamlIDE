using System.Text;

namespace AXamlIDE.Services.Mcp;

/// <summary>
/// A language server's hover Markdown made plain for a model to read: Roslyn escapes punctuation for Markdown
/// ("StartViewModel\.NewProject\(\)") and joins words with &amp;nbsp;, which only cost tokens outside a renderer.
/// Code blocks (```csharp) are kept as they are.
/// </summary>
public static class HoverText
{
    public static string Plain(string markdown)
    {
        var text = new StringBuilder(markdown.Length);
        var inCode = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", System.StringComparison.Ordinal))
            {
                inCode = !inCode;
                text.Append(line.TrimEnd()).Append('\n');
                continue;
            }
            if (inCode)
            {
                text.Append(line).Append('\n');
                continue;
            }

            text.Append(Unescape(line.Replace("&nbsp;", " ")).TrimEnd()).Append('\n');
        }
        return CollapseBlankLines(text.ToString()).Trim();
    }

    /// <summary>"\(" to "(": a backslash before ASCII punctuation is Markdown's escape.</summary>
    private static string Unescape(string line)
    {
        var text = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length && !char.IsAsciiLetterOrDigit(line[i + 1])
                && line[i + 1] is > ' ' and < (char)127)
            {
                text.Append(line[i + 1]);
                i++;
            }
            else
                text.Append(line[i]);
        }
        return text.ToString();
    }

    /// <summary>At most one blank line in a row.</summary>
    private static string CollapseBlankLines(string text)
    {
        var result = new StringBuilder(text.Length);
        var newlines = 0;
        foreach (var c in text)
        {
            newlines = c == '\n' ? newlines + 1 : 0;
            if (newlines <= 2)
                result.Append(c);
        }
        return result.ToString();
    }
}
