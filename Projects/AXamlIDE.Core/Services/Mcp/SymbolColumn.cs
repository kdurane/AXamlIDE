namespace AXamlIDE.Services.Mcp;

/// <summary>
/// Where a name is on a line. The MCP code tools are pointed at code by line and the name as written there
/// ("line 42, symbol Render"), since a model reads text and is bad at counting columns; this finds the column.
/// </summary>
public static class SymbolColumn
{
    /// <summary>
    /// The 0-based index in <paramref name="line"/> where <paramref name="symbol"/> starts: the occurrence that stands
    /// as a whole name (not inside a longer one: "Name" isn't found in "FileName"), else any occurrence. With a
    /// 1-based <paramref name="near"/> column, the occurrence closest to it. Null when it isn't on the line.
    /// </summary>
    public static int? Find(string line, string symbol, int? near = null)
    {
        if (symbol.Length == 0)
            return null;

        int? best = null, bestLoose = null;
        for (var index = line.IndexOf(symbol, System.StringComparison.Ordinal);
             index >= 0;
             index = line.IndexOf(symbol, index + 1, System.StringComparison.Ordinal))
        {
            if (IsWhole(line, index, symbol.Length))
                best = Closer(best, index, near);
            else
                bestLoose = Closer(bestLoose, index, near);
        }
        return best ?? bestLoose;
    }

    private static int Closer(int? current, int candidate, int? near)
    {
        if (current is not { } existing)
            return candidate;
        if (near is not { } column)
            return existing;   // the first one
        var target = column - 1;
        return System.Math.Abs(candidate - target) < System.Math.Abs(existing - target) ? candidate : existing;
    }

    private static bool IsWhole(string line, int start, int length)
        => (start == 0 || !IsNamePart(line[start - 1])) && (start + length >= line.Length || !IsNamePart(line[start + length]));

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
