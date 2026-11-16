using System.Collections.Generic;
using System.Text.Json;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Services.Xaml;

/// <summary>
/// Turns offsets in a XAML file into LSP lines and characters (UTF-16 code units, the same as string indexes), for
/// the IDE's own XAML diagnostics.
/// </summary>
internal sealed class XamlLineMap
{
    private readonly List<int> _starts = [0];

    public XamlLineMap(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                _starts.Add(i + 1);
    }

    public LspDiagnostic Diagnostic(int start, int end, int severity, string message, JsonElement? raw = null)
    {
        var (startLine, startChar) = Position(start);
        var (endLine, endChar) = Position(end);
        return new LspDiagnostic(startLine, startChar, endLine, endChar, severity, message, raw);
    }

    private (int Line, int Char) Position(int offset)
    {
        var line = _starts.BinarySearch(offset);
        if (line < 0)
            line = ~line - 1;
        return (line, offset - _starts[line]);
    }
}
