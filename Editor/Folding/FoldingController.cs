using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using AvaloniaEdit.Folding;
using AXamlIDE.Models;

namespace AXamlIDE.Editor.Folding;

/// <summary>
/// Code folding for the shared editor. Recomputes folds 250ms after the user stops typing.
/// Which folds are collapsed is kept on the document (<see cref="OpenDocument.CollapsedFolds"/>, by start line)
/// so the workspace session can save it, and restored folds are applied once the folds are computed.
/// </summary>
public sealed class FoldingController : IDisposable
{
    private readonly EditorContext _ctx;

    private readonly DispatcherTimer _timer =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    private FoldingManager? _foldingManager;
    private FoldingCaretMargin? _foldingCaretMargin;
    private OpenDocument? _doc;   // the document the folding manager is installed for

    public FoldingController(EditorContext ctx)
    {
        _ctx = ctx;

        // FoldingManager installs document-bound generators; detach them while
        // the old document is still attached to the editor.
        _ctx.Detaching += _ => Uninstall();
        _ctx.Attached += Configure;
        _ctx.TextChanged += _ =>
        {
            _timer.Stop();
            _timer.Start();
        };

        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (_ctx.Active is { } doc)
                Update(doc);
        };
    }

    public void Dispose() => _timer.Stop();

    private void Uninstall()
    {
        var editor = _ctx.Editor;

        if (_doc is not null)
        {
            Capture(_doc);
            _doc.FoldsRestoreRequested -= OnFoldsRestoreRequested;
            _doc = null;
        }

        if (_foldingCaretMargin is not null)
        {
            editor.TextArea.LeftMargins.Remove(_foldingCaretMargin);
            _foldingCaretMargin = null;
        }

        // Remove folding from the previously active document.
        if (_foldingManager is not null)
        {
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }
    }

    private void Configure(OpenDocument? doc)
    {
        Uninstall();

        if (doc is null || !IsFoldableFile(doc.Path))
            return;

        var editor = _ctx.Editor;
        _foldingManager = FoldingManager.Install(editor.TextArea);
        var defaultMargin = editor.TextArea.LeftMargins.OfType<FoldingMargin>().FirstOrDefault();
        if (defaultMargin is not null)
            editor.TextArea.LeftMargins.Remove(defaultMargin);
        _foldingCaretMargin = new FoldingCaretMargin(_foldingManager, () => Capture(doc));
        editor.TextArea.LeftMargins.Add(_foldingCaretMargin);
        _doc = doc;
        doc.FoldsRestoreRequested += OnFoldsRestoreRequested;

        Update(doc);
    }

    private void Update(OpenDocument doc)
    {
        if (!ReferenceEquals(doc, _ctx.Active) || _foldingManager is null)
            return;

        if (doc.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
        {
            new XmlFoldingStrategy().UpdateFoldings(_foldingManager, doc.Document);
        }
        else
        {
            _foldingManager.UpdateFoldings(CSharpFoldingStrategy.CreateFoldings(doc.Document), -1);
        }

        // Restored folds wait for the folds to exist; after that, edits move fold lines, so re-record them.
        if (doc.PendingFolds is { } pending)
            Apply(doc, pending);
        else
            Capture(doc);

        _foldingCaretMargin?.InvalidateVisual();
    }

    private void OnFoldsRestoreRequested()
    {
        if (_doc is { PendingFolds: { } pending } doc && _foldingManager is not null)
        {
            Apply(doc, pending);
            _foldingCaretMargin?.InvalidateVisual();
        }
    }

    /// <summary>Collapse exactly the folds starting on <paramref name="lines"/> (a restored usings fold may be open).</summary>
    private void Apply(OpenDocument doc, IReadOnlyList<int> lines)
    {
        if (_foldingManager is null)
            return;

        var collapsed = lines.ToHashSet();
        foreach (var folding in _foldingManager.AllFoldings)
            folding.IsFolded = collapsed.Contains(doc.Document.GetLineByOffset(folding.StartOffset).LineNumber);

        doc.ClearPendingFolds();
        Capture(doc);
    }

    private void Capture(OpenDocument doc)
    {
        if (_foldingManager is null || doc.PendingFolds is not null)
            return;

        doc.CollapsedFolds =
        [
            .. _foldingManager.AllFoldings
                .Where(folding => folding.IsFolded)
                .Select(folding => doc.Document.GetLineByOffset(folding.StartOffset).LineNumber)
                .Distinct()
        ];
    }

    private static bool IsFoldableFile(string path)
        => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
