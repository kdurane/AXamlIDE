using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AXamlIDE.Services.ColorValues;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Editor.ColorSwatches;

/// <summary>
/// Colour swatches in AXAML and C# (VS Code's colour decorators, Rider's colour previews), and the colour
/// picker behind them: click a swatch and EditorPaneView.axaml's ColorPopup opens on that colour. Picking
/// rewrites the colour in place as it changes, in the same style (Core's ColorLiterals.Format), and the whole
/// visit to the picker is one undo step.
/// </summary>
public sealed class ColorSwatchController
{
    private readonly EditorContext _ctx;
    private readonly Popup _popup;
    private readonly ColorView _picker;
    private readonly ColorSwatchGenerator _generator;

    // The colour being edited: where it starts, how long its text is now, and how it was written to begin with.
    private TextAnchor? _start;
    private int _length;
    private ColorLiteral? _original;
    private bool _settingPicker;

    public ColorSwatchController(EditorContext ctx, Popup popup, ColorView picker)
    {
        _ctx = ctx;
        _popup = popup;
        _picker = picker;

        var textView = ctx.Editor.TextArea.TextView;
        _generator = new ColorSwatchGenerator((line, color) => new ColorSwatch(line, color, ctx.Editor.FontSize,
            textView.DefaultLineHeight, textView.DefaultBaseline, OpenPicker));
        textView.ElementGenerators.Add(_generator);

        ctx.Detaching += _ => ClosePicker();   // before the editor's document (and its undo stack) changes
        ctx.Attached += _ => ShowSwatches();
        ctx.FeaturesChanged += ShowSwatches;

        popup.PlacementTarget = textView;
        popup.Closed += (_, _) => EndEdit();
        picker.ColorChanged += (_, e) => OnPickerColorChanged(e.NewColor);
    }

    /// <summary>Swatches for the document's language, unless they're switched off in the settings.</summary>
    private void ShowSwatches()
    {
        _generator.Find = !_ctx.Settings.Features.ColorSwatches ? null : _ctx.Active?.LanguageId switch
        {
            LanguageIds.Axaml => ColorLiterals.FindInXaml,
            LanguageIds.CSharp => ColorLiterals.FindInCSharp,
            _ => null
        };
        _ctx.Editor.TextArea.TextView.Redraw();
    }

    private void OpenPicker(ColorSwatch swatch)
    {
        var document = _ctx.Editor.Document;
        if (_ctx.Editor.IsReadOnly || _generator.ColorAt(document, swatch.Line, swatch.Color.Start) is not { } color)
            return;   // read-only (decompiled), or the text changed under it

        EndEdit();
        _start = document.CreateAnchor(swatch.Line.Offset + color.Start);
        _start.MovementType = AnchorMovementType.BeforeInsertion;   // stays at the start as the colour is rewritten
        _start.SurviveDeletion = true;
        _length = color.Length;
        _original = color;
        document.UndoStack.StartUndoGroup();

        _settingPicker = true;
        _picker.Color = Color.FromArgb(color.Color.A, color.Color.R, color.Color.G, color.Color.B);
        _settingPicker = false;

        var textView = _ctx.Editor.TextArea.TextView;
        var at = swatch.TranslatePoint(new Point(0, swatch.Bounds.Height), textView) ?? default;
        _popup.PlacementRect = new Rect(at.X, at.Y, 1, 1);
        _popup.IsOpen = true;
    }

    private void OnPickerColorChanged(Color picked)
    {
        if (_settingPicker || _start is not { IsDeleted: false } start || _original is null)
            return;

        var text = ColorLiterals.Format(_original, new ArgbColor(picked.A, picked.R, picked.G, picked.B));
        var document = _ctx.Editor.Document;
        if (start.Offset + _length > document.TextLength || document.GetText(start.Offset, _length) == text)
            return;
        document.Replace(start.Offset, _length, text);
        _length = text.Length;
    }

    private void ClosePicker()
    {
        if (_popup.IsOpen)
            _popup.IsOpen = false;
        EndEdit();
    }

    private void EndEdit()
    {
        if (_start is null)
            return;
        _start = null;
        _original = null;
        _ctx.Editor.Document.UndoStack.EndUndoGroup();
    }
}
