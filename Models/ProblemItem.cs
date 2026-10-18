using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Models;

/// <summary>One row in the Problems panel.</summary>
/// <param name="Severity">An <see cref="LspDiagnosticSeverity"/> value.</param>
public sealed record ProblemItem(OpenDocument Document, int Severity, int Line, int Offset, string Message)
{
    public string SeverityText => Severity switch
    {
        LspDiagnosticSeverity.Error => "Error",
        LspDiagnosticSeverity.Warning => "Warning",
        _ => "Info"
    };
}
