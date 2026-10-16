using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace AXamlIDE.EditorFeatures
{
    /// <summary>
    /// Grammar selection and the editor half of theme colouring. Replaces MainWindow's
    /// SetGrammarForFile, TextMateInstallationOnAppliedTheme, ApplyThemeColorsToEditor and
    /// ApplyBrushAction.
    ///
    /// ApplyThemeColorsToWindow touched the status bar and the window itself, so it stays in
    /// MainWindow and subscribes to <see cref="ThemeApplied"/>. In MainWindow add
    ///   using static AXaxmlIDE.EditorFeatures.TextMateController;
    /// so its remaining ApplyBrushAction calls compile unchanged.
    ///
    /// Construct it exactly where <c>_textMate.AppliedTheme += ...</c> used to be, i.e. BEFORE
    /// the first _textMate.SetTheme(...) call.
    /// </summary>
    public sealed class TextMateController
    {
        private readonly EditorContext _ctx;
        private readonly TextMate.Installation _textMate;
        private readonly RegistryOptions? _registryOptions;
        private readonly CustomMargin? _customMargin;

        public TextMateController(
            EditorContext ctx,
            TextMate.Installation textMate,
            RegistryOptions? registryOptions,
            CustomMargin? customMargin)
        {
            _ctx = ctx;
            _textMate = textMate;
            _registryOptions = registryOptions;
            _customMargin = customMargin;

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
                // (If your AvaloniaEdit.TextMate version rejects null here, pass an unknown scope instead.)
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
                        // Todo: VS Code didn't seem to have a border but it might be nice to have
                        // that option. For now just make it the same..
                    }))
            {
                editor.TextArea.TextView.SetDefaultHighlightLineColors();
            }

            //Todo: looks like the margin doesn't have a active line highlight, would be a nice addition
            if (!ApplyBrushAction(e, "editorLineNumber.foreground",
                    brush => editor.LineNumbersForeground = brush))
            {
                editor.LineNumbersForeground = editor.Foreground;
            }

            // Was in ApplyThemeColorsToWindow, but the side-bar margin is part of the editor.
            if (!ApplyBrushAction(e, "sideBar.background", brush =>
                {
                    if (_customMargin is not null)
                        _customMargin.BackGroundBrush = brush;
                }))
            {
                _customMargin?.SetDefaultBackgroundBrush();
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
}
