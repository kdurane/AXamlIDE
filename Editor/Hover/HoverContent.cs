using System;
using System.Collections.Generic;
using System.Linq;
using Markdig;
using Markdig.Syntax;

namespace AXamlIDE.Editor.Hover;

/// <param name="Severity">An LspDiagnosticSeverity value.</param>
public sealed record HoverDiagnostic(int Severity, string Message);

/// <summary>
/// What a hover card shows, the same way for every language: the diagnostics under the pointer, the symbol's
/// signature (code), and its documentation (text). C# hovers come from Roslyn as markdown (a code block with
/// the signature, then paragraphs); AXAML hovers from the XAML engine as "signature, then summary" lines.
/// Both are read into this shape so <see cref="HoverCard"/> draws them alike.
/// </summary>
public sealed record HoverContent(IReadOnlyList<HoverDiagnostic> Diagnostics, string? Signature, string? Documentation)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public bool IsEmpty => Diagnostics.Count == 0 && string.IsNullOrWhiteSpace(Signature) && string.IsNullOrWhiteSpace(Documentation);

    /// <summary>A language server's hover: code blocks are the signature, everything else the documentation.</summary>
    public static (string? Signature, string? Documentation) FromMarkdown(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var code = new List<string>();
        var text = new List<string>();
        foreach (var block in document)
        {
            if (block is CodeBlock codeBlock)
                code.Add(codeBlock.Lines.ToString().TrimEnd());
            else
                text.Add(Markdown.ToPlainText(markdown.Substring(block.Span.Start, block.Span.Length), Pipeline).Trim());
        }
        return (NullIfEmpty(string.Join("\n", code)), NullIfEmpty(string.Join("\n\n", text.Where(part => part.Length > 0))));
    }

    /// <summary>Plain hover text (the XAML engine's): the first line is the signature, the rest the documentation.</summary>
    public static (string? Signature, string? Documentation) FromPlainText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', 2);
        return (NullIfEmpty(lines[0].Trim()), lines.Length > 1 ? NullIfEmpty(lines[1].Trim()) : null);
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
