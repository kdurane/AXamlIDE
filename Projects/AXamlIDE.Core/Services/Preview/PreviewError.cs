using System.Text.RegularExpressions;

namespace AXamlIDE.Services.Preview;

/// <summary>
/// Why the designer couldn't show a document, and where (1-based line and column, when known).
/// Shown in the Problems tab; the preview itself only says there's an error.
/// </summary>
public sealed partial record PreviewError(string Message, int? Line, int? Column)
{
    /// <summary>
    /// From the designer's report. When it gives no position, one written in the message
    /// ("Line 3, position 17") is used, and taken out of the message either way.
    /// </summary>
    public static PreviewError From(string message, int? line, int? column)
    {
        var match = PositionInMessage().Match(message);
        if (match.Success)
        {
            line ??= int.Parse(match.Groups["line"].Value);
            column ??= int.Parse(match.Groups["column"].Value);
            message = message.Remove(match.Index, match.Length).Trim().TrimEnd(',', ';').Trim();
        }
        return new PreviewError(message, line is > 0 ? line : null, column is > 0 ? column : null);
    }

    [GeneratedRegex(@"\s*\(?\s*[Ll]ine (?<line>\d+),\s*position (?<column>\d+)\s*\)?\.?")]
    private static partial Regex PositionInMessage();
}
