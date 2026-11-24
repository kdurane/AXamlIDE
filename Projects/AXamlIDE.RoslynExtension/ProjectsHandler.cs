using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;

namespace AXamlIDE.RoslynExtension;

/// <summary>
/// The solution's projects with their versions, for the IDE to see what changed since it last asked (an edit, a file
/// changed on disk, a reference added). Cheap: Roslyn keeps the versions, nothing is analysed.
/// </summary>
public sealed class ProjectsHandler : IExtensionWorkspaceMessageHandler<ProjectsRequest, ProjectsResponse>
{
    public async Task<ProjectsResponse> ExecuteAsync(ProjectsRequest message, ExtensionWorkspaceMessageContext context,
        CancellationToken cancellationToken)
    {
        var solution = context.Solution;

        var priority = new HashSet<ProjectId>();
        foreach (var path in message.PriorityPaths ?? [])
            foreach (var documentId in solution.GetDocumentIdsWithFilePath(path))
                priority.Add(documentId.ProjectId);

        var projects = new List<ProjectEntry>();
        foreach (var project in solution.Projects
                     .Where(project => project.SupportsCompilation)
                     .OrderBy(project => priority.Contains(project.Id) ? 0 : 1))
        {
            var version = await project.GetDependentVersionAsync(cancellationToken);
            projects.Add(new ProjectEntry(project.Id.Id.ToString(), project.Name, project.FilePath, version.ToString()));
        }
        return new ProjectsResponse(projects);
    }
}
