using System.Collections.Generic;
using System.Threading.Tasks;
using AXamlIDE.Models;
using AXamlIDE.Services.Search;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Services.Documents;

/// <summary>
/// The app's <see cref="IDocumentHost"/>: opening a file adds it to <see cref="DocumentManager"/>, and
/// activating it sets <see cref="DocumentManager.Active"/>, which every tab strip follows.
/// </summary>
public sealed class DocumentHost(DocumentManager documents, WorkspaceModel workspace) : IDocumentHost
{
    private readonly WorkspaceSearchService _search = new();

    public OpenDocument? Active => documents.Active;

    public OpenDocument Open(string path) => documents.Open(path);

    public OpenDocument OpenReadOnly(string path, string text, string languageId)
        => documents.OpenReadOnly(path, text, languageId);

    public void Activate(OpenDocument document) => documents.Active = document;

    public async Task<IReadOnlyList<string>> GetWorkspaceFilesAsync()
        => workspace.RootDirectory is { } root ? await _search.ListWorkspaceFilesAsync(root) : [];
}
