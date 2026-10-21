namespace AXamlIDE.Services.Indentation;

/// <summary>Whether a file indents with tabs or spaces, so new indentation matches what's already there.</summary>
public static class IndentDetection
{
    /// <summary>True when more indented lines start with a tab than with a space. A file with no indentation yet counts as spaces.</summary>
    public static bool UsesTabs(string text)
    {
        int tabs = 0, spaces = 0;
        var atLineStart = true;
        foreach (var c in text)
        {
            if (atLineStart)
            {
                if (c == '\t')
                    tabs++;
                else if (c == ' ')
                    spaces++;
            }
            atLineStart = c == '\n';
        }
        return tabs > spaces;
    }
}
