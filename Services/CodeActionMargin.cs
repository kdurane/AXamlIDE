using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AXaxmlIDE.Models;

namespace AXaxmlIDE.Services;

internal sealed class CodeActionMargin : AbstractMargin
{
    private const double MarginWidth = 20;

    private readonly Func<int, Control, Task> _showActions;
    private readonly Func<bool> _isEnabled;
    private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.Parse("#FFD166"));
    private static readonly Geometry MarkerGeometry = Geometry.Parse(
        "M5 13a1 1 0 0 1-1-1c0-1.201-.023-1.338-.768-2.357a6 6 0 1 1 9.536 0C12.023 10.663 12 10.799 12 12a1 1 0 0 1-1 1v2a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1zm6.18-4.573-.014.018-.017.024c-.342.468-.738 1.01-.95 1.68a4 4 0 0 0-.168.851H5.97a4 4 0 0 0-.167-.851c-.213-.67-.609-1.212-.95-1.68l-.018-.024-.014-.018a4 4 0 1 1 6.36 0M9 13H7v1h2z");
    private OpenDocument? _document;

    public CodeActionMargin(Func<int, Control, Task> showActions, Func<bool> isEnabled)
    {
        _showActions = showActions;
        _isEnabled = isEnabled;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Show quick fixes and refactorings");
    }

    public void SetDocument(OpenDocument? document)
    {
        _document = document;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        if (newTextView is not null)
            newTextView.VisualLinesChanged += OnVisualLinesChanged;

        base.OnTextViewChanged(oldTextView, newTextView);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var line = GetLineNumber(e);
        if (_document is not null && _isEnabled() && line > 0 && LineHasDiagnostic(line))
        {
            _ = _showActions(line, this);
            e.Handled = true;
        }

        base.OnPointerPressed(e);
    }

    public override void Render(DrawingContext context)
    {
        // Transparent fill over the whole margin so the entire gutter is hit-testable.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        if (_document is null || !_isEnabled() || TextView?.VisualLinesValid != true)
            return;

        foreach (var visualLine in TextView.VisualLines)
        {
            var line = visualLine.FirstDocumentLine.LineNumber;
            if (!LineHasDiagnostic(line))
                continue;

            var centerY = visualLine.VisualTop - TextView.VerticalOffset + visualLine.Height / 2;
            using (context.PushTransform(Matrix.CreateTranslation(Bounds.Width / 2 - 8, centerY - 8)))
                context.DrawGeometry(MarkerBrush, null, MarkerGeometry);
        }
    }

    private bool LineHasDiagnostic(int line) =>
        _document?.Diagnostics.Any(segment =>
            _document.Document.GetLineByOffset(segment.StartOffset).LineNumber == line) == true;

    private int GetLineNumber(PointerEventArgs e)
    {
        if (TextView?.VisualLinesValid != true)
            return -1;

        var visualY = e.GetPosition(TextView).Y + TextView.VerticalOffset;
        return TextView.GetVisualLineFromVisualTop(visualY)?.FirstDocumentLine.LineNumber ?? -1;
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();
}