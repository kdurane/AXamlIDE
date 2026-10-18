using System;
using System.Linq;
using Avalonia.Threading;
using AvaloniaEdit.Folding;
using AXamlIDE.Models;

namespace AXamlIDE.Views.Editor.Folding;

/// <summary>
/// Code folding for the shared editor. Recomputes folds 250ms after the user stops typing.
/// </summary>
public sealed class FoldingController : IDisposable
{
    private readonly EditorContext _ctx;

    private readonly DispatcherTimer _timer =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    private FoldingManager? _foldingManager;
    private FoldingCaretMargin? _foldingCaretMargin;

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
        _foldingCaretMargin = new FoldingCaretMargin(_foldingManager);
        editor.TextArea.LeftMargins.Add(_foldingCaretMargin);

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

        _foldingCaretMargin?.InvalidateVisual();
    }

    private static bool IsFoldableFile(string path)
        => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
