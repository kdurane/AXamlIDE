using System.Collections.Generic;
using System.Threading.Tasks;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Documents;

/// <summary>
/// Opens files and brings their tab to the front. Implemented by the main window so the
/// editor can open documents without knowing about tabs or view models.
/// </summary>
public interface IDocumentHost
{
    OpenDocument Open(string path);
    OpenDocument OpenReadOnly(string path, string text, string languageId);
    void Activate(OpenDocument document);
    Task<IReadOnlyList<string>> GetWorkspaceFilesAsync();
}

public static class DocumentHostExtensions
{
    /// <summary>Open the file (or find it if already open) and make it the active tab.</summary>
    public static OpenDocument OpenAndActivate(this IDocumentHost host, string path)
    {
        var document = host.Open(path);
        host.Activate(document);
        return document;
    }
}
