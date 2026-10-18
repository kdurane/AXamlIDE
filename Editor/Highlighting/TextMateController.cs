using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.TextMate;
using AXamlIDE.Views.Editor.Debugging;
using TextMateSharp.Grammars;

namespace AXamlIDE.Views.Editor.Highlighting;

/// <summary>
/// Grammar selection and the editor half of theme colouring. The window-level colours
/// (status bar, window background) are applied by MainWindow, which listens for
/// EditorThemeAppliedMessage and reuses <see cref="ApplyBrushAction"/>.
///
/// Must be constructed before the first SetTheme call, or the first theme is missed.
/// </summary>
public sealed class TextMateController
{
    private readonly EditorContext _ctx;
    private readonly TextMate.Installation _textMate;
    private readonly RegistryOptions? _registryOptions;
    private readonly BreakpointMargin? _breakpointMargin;

    public TextMateController(
        EditorContext ctx,
        TextMate.Installation textMate,
        RegistryOptions? registryOptions,
        BreakpointMargin? breakpointMargin)
    {
        _ctx = ctx;
        _textMate = textMate;
        _registryOptions = registryOptions;
        _breakpointMargin = breakpointMargin;

        _textMate.AppliedTheme += OnAppliedTheme;
    }

    /// <summary>Raised after the editor colours are applied. MainWindow colours the status bar and window.</summary>
    public event Action<TextMate.Installation>? ThemeApplied;

    public void SetGrammarForFile(string path)
    {
        var language = string.IsNullOrEmpty(path)
            ? null
            : _registryOptions?.GetLanguageByExtension(Path.GetExtension(path));

        if (language is null)
        {
            // Don't leave the previous file's grammar applied.
            _textMate.SetGrammar(null);
            return;
        }

        _textMate.SetGrammar(_registryOptions?.GetScopeByLanguageId(language.Id));
    }

    private void OnAppliedTheme(object? sender, TextMate.Installation e)
    {
        ApplyThemeColorsToEditor(e);
        ThemeApplied?.Invoke(e);
    }

    private void ApplyThemeColorsToEditor(TextMate.Installation e)
    {
        var editor = _ctx.Editor;

        ApplyBrushAction(e, "editor.background", brush => editor.Background = brush);
        ApplyBrushAction(e, "editor.foreground", brush => editor.Foreground = brush);

        if (!ApplyBrushAction(e, "editor.selectionBackground",
                brush => editor.TextArea.SelectionBrush = brush))
        {
            if (Application.Current!.TryGetResource("TextAreaSelectionBrush", out var resourceObject))
            {
                if (resourceObject is IBrush brush)
                {
                    editor.TextArea.SelectionBrush = brush;
                }
            }
        }

        if (!ApplyBrushAction(e, "editor.lineHighlightBackground",
                brush =>
                {
                    editor.TextArea.TextView.CurrentLineBackground = brush;
                    editor.TextArea.TextView.CurrentLineBorder = new Pen(brush);
                    // VS Code has no current-line border; use the same colour so it doesn't show.
                }))
        {
            editor.TextArea.TextView.SetDefaultHighlightLineColors();
        }

        // TODO: highlight the current line number in the margin too.
        if (!ApplyBrushAction(e, "editorLineNumber.foreground",
                brush => editor.LineNumbersForeground = brush))
        {
            editor.LineNumbersForeground = editor.Foreground;
        }

        if (!ApplyBrushAction(e, "sideBar.background", brush =>
            {
                if (_breakpointMargin is not null)
                    _breakpointMargin.BackgroundBrush = brush;
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
