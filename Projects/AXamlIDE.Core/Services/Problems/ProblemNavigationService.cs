using System.Collections.Generic;
using System.Linq;

namespace AXamlIDE.Services.Problems;

public sealed class ProblemNavigationService
{
    public (TDocument Document, int Offset)? FindNext<TDocument>(
        IEnumerable<(TDocument Document, int Offset)> problems,
        IReadOnlyList<TDocument> documents,
        TDocument? activeDocument,
        int caretOffset,
        bool forward)
        where TDocument : class
    {
        var documentOrder = documents
            .Select((document, index) => (document, index))
            .ToDictionary(item => item.document, item => item.index);
        var ordered = problems
            .Where(problem => documentOrder.ContainsKey(problem.Document))
            .OrderBy(problem => documentOrder[problem.Document])
            .ThenBy(problem => problem.Offset)
            .ToArray();
        if (ordered.Length == 0)
            return null;

        var activeDocumentIndex = activeDocument is not null
                                  && documentOrder.TryGetValue(activeDocument, out var index)
            ? index
            : -1;
        var nextIndex = forward
            ? System.Array.FindIndex(ordered, problem =>
                documentOrder[problem.Document] > activeDocumentIndex
                || documentOrder[problem.Document] == activeDocumentIndex
                && problem.Offset > caretOffset)
            : System.Array.FindLastIndex(ordered, problem =>
                documentOrder[problem.Document] < activeDocumentIndex
                || documentOrder[problem.Document] == activeDocumentIndex
                && problem.Offset < caretOffset);

        var selected = nextIndex >= 0
            ? ordered[nextIndex]
            : forward ? ordered[0] : ordered[^1];
        return (selected.Document, selected.Offset);
    }
}
