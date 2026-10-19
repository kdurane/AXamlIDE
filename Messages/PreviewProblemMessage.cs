using AXamlIDE.Models;
using AXamlIDE.Services.Preview;

namespace AXamlIDE.Messages;

/// <summary>The preview of <paramref name="Document"/> failed (<paramref name="Error"/>), or worked again (null).</summary>
public sealed record PreviewProblemMessage(OpenDocument Document, PreviewError? Error);
