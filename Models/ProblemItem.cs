
namespace AXamlIDE.Models;

public sealed record ProblemItem(OpenDocument Document, string Severity, int Line, int Offset, string Message);
