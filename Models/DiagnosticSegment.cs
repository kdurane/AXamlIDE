using System.Text.Json;
using AvaloniaEdit.Document;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Models;

/// <summary>One diagnostic, anchored to the document text so it moves as the user types.</summary>
public sealed class DiagnosticSegment : TextSegment
{
    public string Message { get; init; } = "";
    public int Severity { get; init; }   // an LspDiagnosticSeverity value
    public JsonElement? RawDiagnostic { get; init; }

    /// <summary>
    /// The code isn't needed (an unused using, an unused private member): drawn faded rather than squiggled, unless
    /// it's also a warning or an error. Roslyn sends these as hints with the Unnecessary tag.
    /// </summary>
    public bool IsUnnecessary { get; init; }

    /// <summary>Drawn with a squiggle: everything but a hint (a hint is only faded, if it's anything).</summary>
    public bool IsSquiggled => Severity < LspDiagnosticSeverity.Hint;
}
