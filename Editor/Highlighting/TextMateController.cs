using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.TextMate;
using AXamlIDE.Editor.Debugging;

namespace AXamlIDE.Editor.Highlighting;

/// <summary>
/// Grammar selection and the editor's own theme colours (background, selection, line numbers...).
/// The rest of the UI is coloured from the same theme by Views/ThemeSwitcher.
///
/// Must be constructed before the first SetTheme call, or the first theme is missed.
/// </summary>
public sealed class TextMateController
{
    private readonly EditorContext _ctx;
    private readonly TextMate.Installation _textMate;
    private readonly EditorGrammars _grammars;
    private readonly BreakpointMargin? _breakpointMargin;
    private readonly SelectionRenderer _selection;
    private readonly CurrentLineRenderer _currentLine;

    public TextMateController(
        EditorContext ctx,
        TextMate.Installation textMate,
        EditorGrammars grammars,
        BreakpointMargin? breakpointMargin,
        SelectionRenderer selection,
        CurrentLineRenderer currentLine)
    {
        _ctx = ctx;
        _textMate = textMate;
        _grammars = grammars;
        _breakpointMargin = breakpointMargin;
        _selection = selection;
        _currentLine = currentLine;

        _textMate.AppliedTheme += OnAppliedTheme;
    }

    /// <summary>The file's grammar (AXAML has its own, see EditorGrammars); none clears the previous file's.</summary>
    public void SetGrammarForFile(string path) => _textMate.SetGrammar(_grammars.ScopeForFile(path));

    private void OnAppliedTheme(object? sender, TextMate.Installation e)
    {
        ApplyThemeColorsToEditor(e);
    }

    private void ApplyThemeColorsToEditor(TextMate.Installation e)
    {
        var editor = _ctx.Editor;

        ApplyBrushAction(e, "editor.background", brush => editor.Background = brush);
        ApplyBrushAction(e, "editor.foreground", brush => editor.Foreground = brush);

        if (!ApplyBrushAction(e, "editor.selectionBackground", brush => _selection.Brush = brush))
        {
            if (Application.Current!.TryGetResource("TextAreaSelectionBrush", out var resourceObject))
            {
                if (resourceObject is IBrush brush)
                {
                    _selection.Brush = brush;
                }
            }
        }

        if (!ApplyBrushAction(e, "editor.lineHighlightBackground",
         brush =>
         {
             _currentLine.Background = brush;
             _currentLine.Border = new Pen(brush);
             // VS Code has no current-line border; use the same colour so it doesn't show.
         }))
        {
            _currentLine.SetDefaultColors();
        }

        // TODO: highlight the current line number in the margin too.
        if (!ApplyBrushAction(e, "editorLineNumber.foreground",
                brush => editor.LineNumbersForeground = brush))
        {
            editor.LineNumbersForeground = editor.Foreground;
        }

        if (!ApplyBrushAction(e, "sideBar.background", brush =>
            {
                _breakpointMargin?.BackgroundBrush = brush;
            }))
        {
            _breakpointMargin?.SetDefaultBackgroundBrush();
        }
    }

    public static bool ApplyBrushAction(TextMate.Installation e, string colorKeyNameFromJson,
        Action<IBrush> applyColorAction)
    {
        if (!e.TryGetThemeColor(colorKeyNameFromJson, out var colorString))
            return false;

        if (!Color.TryParse(colorString, out Color color))
            return false;

        var colorBrush = new SolidColorBrush(color);
        applyColorAction(colorBrush);
        return true;
    }
}
