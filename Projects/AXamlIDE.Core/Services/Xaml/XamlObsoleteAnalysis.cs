using System.Collections.Generic;
using System.Text.Json;
using AXamlIDE.CompletionEngine.AssemblyMetadata;
using AXamlIDE.CompletionEngine.Parsing;
using AXamlIDE.Services.Lsp;
using MetadataHelper = AXamlIDE.CompletionEngine.Completion.CompletionEngine.MetadataHelper;

namespace AXamlIDE.Services.Xaml;

/// <summary>
/// Uses of [Obsolete] types and properties in AXAML (Watermark on a TextBox in Avalonia 12), which the build warns
/// about but the editor otherwise wouldn't. Flagged as C#'s CS0618 is: a warning (an error when the attribute says
/// so) with the message the attribute gives.
///
/// Covers elements (&lt;OldControl&gt;), property elements (&lt;TextBox.Watermark&gt;) and attributes, attached ones
/// included (Owner.Property="..."). Not yet: a Setter's Property="..." in a style, which names the property as a value.
/// </summary>
public static class XamlObsoleteAnalysis
{
    // What Roslyn sends with CS0618, so it can be drawn the same way.
    private static readonly JsonElement DeprecatedRaw = JsonDocument.Parse("{\"tags\":[2]}").RootElement.Clone();

    /// <param name="assemblyName">The project's own assembly, for clr-namespace xmlns without ";assembly=".</param>
    public static IReadOnlyList<LspDiagnostic> Find(Metadata metadata, string text, string? assemblyName)
    {
        var helper = new MetadataHelper();
        helper.SetMetadata(metadata, text, assemblyName);
        var lines = new XamlLineMap(text);
        var found = new List<LspDiagnostic>();

        foreach (var tag in XamlTagScanner.StartTags(text))
        {
            var nameStart = tag.Start + 1;

            // <TextBox.Watermark>: a property element. <OldControl>: a type.
            if (SplitOwner(tag.Name) is var (owner, property))
            {
                if (helper.LookupProperty(owner, property) is { Obsolete: { } obsolete })
                    found.Add(Flag(lines, nameStart, tag.Name.Length, $"{TypeName(owner)}.{property}", obsolete));
            }
            else if (helper.LookupType(tag.Name) is { Obsolete: { } obsolete } type)
            {
                found.Add(Flag(lines, nameStart, tag.Name.Length, type.Name, obsolete));
            }

            foreach (var attribute in tag.Attributes)
            {
                if (attribute.Name.StartsWith("xmlns", System.StringComparison.Ordinal))
                    continue;

                // Grid.Row="1" names its owner; Watermark="..." belongs to the element.
                var (attributeOwner, attributeProperty) = SplitOwner(attribute.Name) ?? (tag.Name, attribute.Name);
                if (helper.LookupProperty(attributeOwner, attributeProperty) is { Obsolete: { } obsolete })
                    found.Add(Flag(lines, attribute.NameStart, attribute.Name.Length,
                        $"{TypeName(attributeOwner)}.{attributeProperty}", obsolete));
            }
        }

        return found;
    }

    private static LspDiagnostic Flag(XamlLineMap lines, int start, int length, string name, MetadataObsolete obsolete)
    {
        var message = obsolete.Message.Length == 0
            ? $"'{name}' is obsolete"
            : $"'{name}' is obsolete: '{obsolete.Message}'";
        var severity = obsolete.IsError ? LspDiagnosticSeverity.Error : LspDiagnosticSeverity.Warning;
        return lines.Diagnostic(start, start + length, severity, message, DeprecatedRaw);
    }

    /// <summary>"local:Owner.Property" as ("local:Owner", "Property"); null when there's no owner.</summary>
    private static (string Owner, string Property)? SplitOwner(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? (name[..dot], name[(dot + 1)..]) : null;
    }

    /// <summary>The type name without its xmlns prefix, as the message names it.</summary>
    private static string TypeName(string name) => name[(name.IndexOf(':') + 1)..];
}
