using System.Text.Json;
using AvaloniaEdit.Document;

namespace AXamlIDE.Models;

/// <summary>One diagnostic, anchored to the document text so it moves as the user types.</summary>
public sealed class DiagnosticSegment : TextSegment
{
    public string Message { get; init; } = "";
    public int Severity { get; init; }   // an LspDiagnosticSeverity value
    public JsonElement? RawDiagnostic { get; init; }
}
