using System.IO;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Models;

/// <summary>Where a Problems row came from: shown in its Source column, and what the filter buttons go by.</summary>
public static class ProblemSource
{
    /// <summary>A language server diagnostic for an open document.</summary>
    public const string IntelliSense = "IntelliSense";

    /// <summary>An error or warning from the last build (the file need not be open).</summary>
    public const string Build = "Build";

    /// <summary>The AXAML previewer couldn't render the document.</summary>
    public const string Preview = "Preview";
}

/// <summary>
/// Which problems the Problems panel lists, like the scope box in Visual Studio's Error List. Shown in the box as
/// they read here.
/// </summary>
public static class ProblemScope
{
    /// <summary>Everything: open documents' diagnostics and the last build's problems in any file.</summary>
    public const string EntireSolution = "Entire Solution";

    /// <summary>Only problems in files that are open in a tab.</summary>
    public const string OpenDocuments = "Open Documents";

    /// <summary>Only problems in the project that the active document belongs to.</summary>
    public const string CurrentProject = "Current Project";

    /// <summary>Only problems in the active document.</summary>
    public const string CurrentDocument = "Current Document";

    public static string[] All { get; } = [EntireSolution, OpenDocuments, CurrentProject, CurrentDocument];
}

/// <summary>
/// One row in the Problems panel. A problem in an open document has its <see cref="Document"/> and an
/// <see cref="Offset"/> in it; a build problem in a file that isn't open has only <see cref="Path"/>,
/// <see cref="Line"/> and <see cref="Column"/> (navigating opens the file). A build problem with no file at all
/// (an MSB error about the project) has an empty path and line 0.
/// </summary>
/// <param name="Severity">An <see cref="LspDiagnosticSeverity"/> value.</param>
public sealed record ProblemItem(
    string Path,
    int Severity,
    int Line,
    int Column,
    string Message,
    string Source,
    string Code = "",
    string? Project = null,
    OpenDocument? Document = null,
    int Offset = -1)
{
    /// <summary>A problem in an open document, at an offset in its text.</summary>
    /// <param name="project">The .csproj the document belongs to, when known.</param>
    public static ProblemItem InDocument(OpenDocument document, int severity, int offset, string message, string source,
        string code = "", string? project = null)
    {
        var location = document.Document.GetLocation(offset);
        return new ProblemItem(document.Path, severity, location.Line, location.Column, message, source, code, project, document, offset);
    }

    public string SeverityText => Severity switch
    {
        LspDiagnosticSeverity.Error => "Error",
        LspDiagnosticSeverity.Warning => "Warning",
        _ => "Info"
    };

    public bool IsError => Severity == LspDiagnosticSeverity.Error;
    public bool IsWarning => Severity == LspDiagnosticSeverity.Warning;
    public bool IsInfo => Severity > LspDiagnosticSeverity.Warning;

    public string FileName => Path.Length > 0 ? System.IO.Path.GetFileName(Path) : "";

    /// <summary>The project's name (from a build problem's "[x.csproj]", or the project that owns the file), or empty.</summary>
    public string ProjectName => Project is { Length: > 0 } ? System.IO.Path.GetFileNameWithoutExtension(Project) : "";

    public string LineText => Line > 0 ? Line.ToString() : "";

    /// <summary>Navigation can take the user there: an open document, or a file with a line.</summary>
    public bool HasLocation => Document is not null || Path.Length > 0 && Line > 0 && File.Exists(Path);

    /// <summary>The row's tooltip: the full path, and the project when known.</summary>
    public string ToolTip => Project is { Length: > 0 } ? $"{Path}\n{Project}" : Path;
}
