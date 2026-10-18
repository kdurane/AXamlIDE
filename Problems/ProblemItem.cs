using AXamlIDE.Documents;

namespace AXamlIDE.Problems;

public sealed record ProblemItem(OpenDocument Document, string Severity, int Line, int Offset, string Message);
