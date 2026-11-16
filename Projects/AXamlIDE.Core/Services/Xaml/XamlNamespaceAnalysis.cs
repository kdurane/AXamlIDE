using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AXamlIDE.CompletionEngine.Parsing;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Services.Xaml;

/// <summary>
/// The IDE's own checks on an AXAML file's xmlns declarations, which Avalonia's compiler and previewer don't report:
///   - an xmlns prefix that nothing uses: faded, like an unused C# using (Rider and Visual Studio do the same);
///   - a prefix in mc:Ignorable that isn't declared (Avalonia builds it anyway; WPF and Rider call it an error);
///   - an element or attribute whose prefix isn't declared.
///
/// Declarations are treated as covering the whole file. AXAML nearly always declares them on the root; when one is
/// declared lower down, a use outside that element still counts, which only ever means not fading something.
/// Comments are skipped: a prefix used only in a comment is unused.
/// </summary>
public static class XamlNamespaceAnalysis
{
    private const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    // What Roslyn sends for an unused using: the Unnecessary tag tells the editor to fade it rather than squiggle it.
    private static readonly JsonElement UnnecessaryRaw = JsonDocument.Parse("{\"tags\":[1]}").RootElement.Clone();

    private sealed record Declaration(string Prefix, string Uri, int Start, int End);

    public static IReadOnlyList<LspDiagnostic> Find(string text)
    {
        var tags = XamlTagScanner.StartTags(text).ToList();
        var declarations = new List<Declaration>();
        var ignorables = new List<(string Prefix, int Start)>();
        var excluded = new List<(int Start, int End)>();   // text the use search skips: xmlns and mc:Ignorable values

        foreach (var tag in tags)
        {
            foreach (var attribute in tag.Attributes)
            {
                if (attribute.Value is not { } value || ValueStart(text, attribute) is not { } valueStart)
                    continue;

                if (attribute.Name.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    declarations.Add(new Declaration(attribute.Name[6..], value, attribute.NameStart,
                        valueStart + value.Length + 1));
                    excluded.Add((valueStart, valueStart + value.Length));
                }
                else if (attribute.Name == "xmlns")
                {
                    excluded.Add((valueStart, valueStart + value.Length));
                }
                else if (Prefix(attribute.Name) is not null && LocalName(attribute.Name) == "Ignorable")
                {
                    ignorables.AddRange(Words(value, valueStart));
                    excluded.Add((valueStart, valueStart + value.Length));
                }
            }
        }

        var declared = declarations.Select(d => d.Prefix).ToHashSet(StringComparer.Ordinal);
        var lines = new XamlLineMap(text);
        var found = new List<LspDiagnostic>();

        // mc:Ignorable="d" with no xmlns:d.
        var ignorableIsMc = declarations.Any(d => d.Uri == MarkupCompatibility);
        foreach (var (prefix, start) in ignorables)
            if (ignorableIsMc && !declared.Contains(prefix))
                found.Add(lines.Diagnostic(start, start + prefix.Length, LspDiagnosticSeverity.Warning,
                    $"The namespace prefix '{prefix}' in mc:Ignorable is not declared"));

        // <local:Foo> or d:DesignWidth with no xmlns for the prefix.
        foreach (var tag in tags)
        {
            if (Prefix(tag.Name) is { } elementPrefix && !IsBuiltIn(elementPrefix) && !declared.Contains(elementPrefix))
                found.Add(lines.Diagnostic(tag.Start + 1, tag.Start + 1 + elementPrefix.Length, LspDiagnosticSeverity.Error,
                    $"The namespace prefix '{elementPrefix}' is not declared"));
            foreach (var attribute in tag.Attributes)
                if (Prefix(attribute.Name) is { } prefix && !IsBuiltIn(prefix) && !declared.Contains(prefix))
                    found.Add(lines.Diagnostic(attribute.NameStart, attribute.NameStart + prefix.Length,
                        LspDiagnosticSeverity.Error, $"The namespace prefix '{prefix}' is not declared"));
        }

        // xmlns:p that nothing uses.
        var used = UsedPrefixes(text, excluded);
        foreach (var declaration in declarations)
            if (!used.Contains(declaration.Prefix))
                found.Add(lines.Diagnostic(declaration.Start, declaration.End, LspDiagnosticSeverity.Hint,
                    $"The namespace declaration 'xmlns:{declaration.Prefix}' is not used", UnnecessaryRaw));

        return found;
    }

    /// <summary>
    /// Every "p:Name" or "p|Name" outside comments and the excluded ranges: element and attribute names, and values
    /// such as x:DataType="vm:Item", {x:Static local:Strings.Title}, (local:Attached.Value) and Selector="local|Foo".
    /// Picking up something that isn't really a prefix ("Button:pointerover") is harmless: it can only keep a
    /// declaration from being faded.
    /// </summary>
    private static HashSet<string> UsedPrefixes(string text, List<(int Start, int End)> excluded)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        while (i < text.Length)
        {
            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                var close = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (close < 0)
                    break;
                i = close + 3;
                continue;
            }
            if (excluded.FirstOrDefault(range => i >= range.Start && i < range.End) is { End: > 0 } skip)
            {
                i = skip.End;
                continue;
            }
            if (!IsNameStart(text[i]) || (i > 0 && IsNameChar(text[i - 1])))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && IsNameChar(text[i]))
                i++;
            if (i + 1 < text.Length && text[i] is ':' or '|' && IsNameStart(text[i + 1]))
                used.Add(text[start..i]);
        }
        return used;
    }

    /// <summary>Where an attribute's value starts in the text (just after its opening quote).</summary>
    private static int? ValueStart(string text, XamlAttribute attribute)
    {
        var quote = text.IndexOfAny(['"', '\''], attribute.NameStart + attribute.Name.Length);
        return quote < 0 ? null : quote + 1;
    }

    /// <summary>The space-separated words of a value, with where each starts.</summary>
    private static IEnumerable<(string Word, int Start)> Words(string value, int valueStart)
    {
        var i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && char.IsWhiteSpace(value[i]))
                i++;
            var start = i;
            while (i < value.Length && !char.IsWhiteSpace(value[i]))
                i++;
            if (i > start)
                yield return (value[start..i], valueStart + start);
        }
    }

    private static string? Prefix(string name)
    {
        var colon = name.IndexOf(':');
        return colon > 0 ? name[..colon] : null;
    }

    private static string LocalName(string name)
    {
        var colon = name.IndexOf(':');
        return colon < 0 ? name : name[(colon + 1)..];
    }

    // xmlns and xml are bound by XML itself, never declared.
    private static bool IsBuiltIn(string prefix) => prefix is "xmlns" or "xml";

    // ':' isn't a name character here: it's what separates the prefix.
    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.';
}
