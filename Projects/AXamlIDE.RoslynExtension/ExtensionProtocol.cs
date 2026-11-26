using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AXamlIDE.RoslynExtension;

// The messages between the IDE and its extension inside the Roslyn language server. Shared (linked into
// AXamlIDE.Core) so the two sides can't drift. The server passes each message as JSON text
// (workspace/_vs_dispatchExtensionMessage { messageName, message }) and serializes the handler's answer with
// System.Text.Json's defaults, so the names here are fixed with attributes.

/// <summary>The handlers' message names: the server registers each handler under its type's full name.</summary>
public static class ExtensionMessages
{
    public const string Projects = "AXamlIDE.RoslynExtension.ProjectsHandler";
    public const string ProjectDiagnostics = "AXamlIDE.RoslynExtension.ProjectDiagnosticsHandler";
    public const string ProjectCompilationDiagnostics = "AXamlIDE.RoslynExtension.ProjectCompilationDiagnosticsHandler";
    public const string ProjectTests = "AXamlIDE.RoslynExtension.ProjectTestsHandler";
}

/// <summary>
/// Ask for the solution's projects and their versions (cheap: no analysis). Projects with a document at one of
/// <see cref="PriorityPaths"/> (the open files) come first, so they're checked first.
/// </summary>
public sealed record ProjectsRequest(
    [property: JsonPropertyName("priorityPaths")] IReadOnlyList<string> PriorityPaths);

public sealed record ProjectsResponse(
    [property: JsonPropertyName("projects")] IReadOnlyList<ProjectEntry> Projects);

/// <summary>
/// One project. <see cref="Version"/> changes whenever anything in it or a project it depends on does (a document,
/// an .editorconfig, a reference, an option), so a project whose version is the same needs no new check.
/// </summary>
public sealed record ProjectEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("filePath")] string? FilePath,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// Ask for one project's compiler diagnostics (errors, warnings and messages; not hidden ones). Two messages take it:
/// <see cref="ExtensionMessages.ProjectDiagnostics"/> checks each file (quick, and only the changed files after an
/// edit); <see cref="ExtensionMessages.ProjectCompilationDiagnostics"/> compiles the whole project and answers with
/// just what the files' checks can't see (unused fields CS0169/CS0414/CS0649, no Main CS5001...), so it's slower and
/// is asked second.
/// </summary>
public sealed record ProjectDiagnosticsRequest(
    [property: JsonPropertyName("projectId")] string ProjectId);

/// <summary>
/// Every file of the project that has a diagnostic (a file that isn't listed has none). <see cref="Version"/> is the
/// project's version these are for. Diagnostics that belong to no file (a bad reference, an option) are listed under
/// the project file. <see cref="Found"/> is false if there's no such project any more. <see cref="Checked"/> is how
/// many files were checked again (the rest came from what the extension kept), and <see cref="Milliseconds"/> how long
/// it took, for the log.
/// </summary>
public sealed record ProjectDiagnosticsResponse(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("files")] IReadOnlyList<FileDiagnostics> Files,
    [property: JsonPropertyName("checked")] int Checked = 0,
    [property: JsonPropertyName("milliseconds")] long Milliseconds = 0);

public sealed record FileDiagnostics(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ExtensionDiagnostic> Diagnostics);

/// <summary>A diagnostic in the LSP's shape, so the IDE reads it as it reads the server's own.</summary>
public sealed record ExtensionDiagnostic(
    [property: JsonPropertyName("range")] ExtensionRange Range,
    [property: JsonPropertyName("severity")] int Severity,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("codeDescription"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExtensionCodeDescription? CodeDescription,
    [property: JsonPropertyName("tags"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? Tags);

public sealed record ExtensionRange(
    [property: JsonPropertyName("start")] ExtensionPosition Start,
    [property: JsonPropertyName("end")] ExtensionPosition End);

public sealed record ExtensionPosition(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

public sealed record ExtensionCodeDescription(
    [property: JsonPropertyName("href")] string Href);

/// <summary>Ask for one project's test methods, read from its code (unsaved edits included; nothing is built).</summary>
public sealed record ProjectTestsRequest(
    [property: JsonPropertyName("projectId")] string ProjectId);

/// <summary>
/// The project's tests: each method with a test attribute of xUnit, NUnit, MSTest or TUnit (or one derived from those),
/// in every class that can run (not abstract), inherited ones included. <see cref="Version"/> is the project's version
/// these are for; <see cref="Found"/> is false if there's no such project any more.
/// </summary>
public sealed record ProjectTestsResponse(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("tests")] IReadOnlyList<TestMethodEntry> Tests);

/// <summary>
/// One test method. <see cref="ClassName"/> is as the test frameworks name it: "Ns.Class", a nested class
/// "Ns.Outer+Inner". <see cref="IsDataDriven"/>: it runs once per data row ([Theory], [TestCase], [DataRow]...), so a
/// run reports cases under it. <see cref="Path"/> and the 0-based <see cref="Line"/> and <see cref="Column"/> are
/// where its name is written (no path for a method inherited from a compiled base class).
/// </summary>
public sealed record TestMethodEntry(
    [property: JsonPropertyName("className")] string ClassName,
    [property: JsonPropertyName("methodName")] string MethodName,
    [property: JsonPropertyName("isDataDriven")] bool IsDataDriven,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column);
