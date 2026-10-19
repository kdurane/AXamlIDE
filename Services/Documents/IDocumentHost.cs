using System.Collections.Generic;
using System.Threading.Tasks;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Documents;

/// <summary>
/// Opens files and brings their tab to the front, so the editor can open documents without
/// knowing about tabs, docks or windows. Implemented by <see cref="DocumentHost"/>.
/// </summary>
public interface IDocumentHost
{
    /// <summary>The selected tab, in whichever window it is (null when no tab is open).</summary>
    OpenDocument? Active { get; }

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
