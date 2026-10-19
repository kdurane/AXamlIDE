using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using AXamlIDE.Editor;
using AXamlIDE.Editor.Diff;
using AXamlIDE.Editor.Highlighting;
using AXamlIDE.Services.Editor;
using AXamlIDE.Services.Git;
using AXamlIDE.Services.Settings;
using AXamlIDE.ViewModels;
using TextMateSharp.Grammars;

namespace AXamlIDE.Views;

/// <summary>
/// A diff tab (DataContext: <see cref="DiffDockable"/>): the two versions side by side in read-only editors,
/// with syntax colours, each side's own line numbers, coloured added/removed lines, and scrolling kept together.
/// Made by Views/DockViewCache; <see cref="Shutdown"/> when the tab is closed.
/// </summary>
public partial class DiffView : UserControl
{
    private readonly DiffDockable _diff = null!;
    private readonly IEditorSettings _settings = null!;
    private readonly RegistryOptions _registryOptions = null!;
    private readonly Side _left = null!;
    private readonly Side _right = null!;
    private bool _syncingScroll;

    /// <summary>For the XAML designer only.</summary>
    public DiffView()
    {
        InitializeComponent();
    }

    public DiffView(DiffDockable diff, EditorServices services) : this()
    {
        _diff = diff;
        _settings = services.Settings;
        _registryOptions = services.RegistryOptions;
        DataContext = diff;

        _left = new Side(LeftEditor, isLeft: true, _registryOptions, diff.FilePath);
        _right = new Side(RightEditor, isLeft: false, _registryOptions, diff.FilePath);
        ApplySettings();

        LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => FollowScroll(LeftEditor, RightEditor);
        RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => FollowScroll(RightEditor, LeftEditor);
        diff.PropertyChanged += OnDiffPropertyChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        Show();
    }

    public void Shutdown()
    {
        _diff.PropertyChanged -= OnDiffPropertyChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        _left.Dispose();
        _right.Dispose();
    }

    private void OnDiffPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffDockable.Diff))
            Show();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => ApplySettings();

    private void ApplySettings()
    {
        var theme = _settings.SelectedTheme.Load(_registryOptions);
        foreach (var side in new[] { _left, _right })
        {
            side.Editor.FontFamily = new FontFamily(_settings.EditorFontFamily);
            side.Editor.FontSize = _settings.EditorFontSize;
            side.TextMate.SetTheme(theme);
        }
    }

    /// <summary>Show the latest diff, keeping the scroll position (it reloads on every save).</summary>
    private void Show()
    {
        if (_diff.Diff is not { } diff)
            return;
        var offset = LeftEditor.VerticalOffset;
        _left.Show(diff.Left);
        _right.Show(diff.Right);
        LeftEditor.ScrollToOffset(LeftEditor.HorizontalOffset, offset);
        RightEditor.ScrollToOffset(RightEditor.HorizontalOffset, offset);
    }

    // Both sides have the same lines (filler included), so the same offset shows the same place.
    private void FollowScroll(TextEditor from, TextEditor to)
    {
        if (_syncingScroll)
            return;
        _syncingScroll = true;
        to.ScrollToOffset(from.HorizontalOffset, from.VerticalOffset);
        _syncingScroll = false;
    }

    private void OnPreviousChangeClick(object? sender, RoutedEventArgs e) => GoToChange(forward: false);

    private void OnNextChangeClick(object? sender, RoutedEventArgs e) => GoToChange(forward: true);

    private void GoToChange(bool forward)
    {
        var current = LeftEditor.TextArea.Caret.Line - 1;
        if (_diff.NextChange(current, forward) is not { } line)
            return;
        foreach (var editor in new[] { LeftEditor, RightEditor })
        {
            editor.TextArea.Caret.Line = line + 1;
            editor.TextArea.Caret.Column = 1;
        }
        LeftEditor.ScrollToLine(line + 1);
    }

    /// <summary>One side: its editor, TextMate colouring, line backgrounds and line numbers.</summary>
    private sealed class Side : IDisposable
    {
        private readonly DiffLineRenderer _renderer;
        private readonly DiffLineNumberMargin _numbers = new();

        public Side(TextEditor editor, bool isLeft, RegistryOptions registryOptions, string filePath)
        {
            Editor = editor;
            _renderer = new DiffLineRenderer(isLeft);
            editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
            editor.TextArea.LeftMargins.Insert(0, _numbers);

            TextMate = editor.InstallTextMate(registryOptions);
            TextMate.AppliedTheme += (_, installation) =>
            {
                TextMateController.ApplyBrushAction(installation, "editor.background", brush => editor.Background = brush);
                TextMateController.ApplyBrushAction(installation, "editor.foreground", brush => editor.Foreground = brush);
                if (!TextMateController.ApplyBrushAction(installation, "editorLineNumber.foreground", brush => _numbers.Foreground = brush))
                    _numbers.Foreground = editor.Foreground;
            };
            if (GrammarFor(registryOptions, filePath) is { } scope)
                TextMate.SetGrammar(scope);
        }

        public TextEditor Editor { get; }
        public TextMate.Installation TextMate { get; }

        public void Show(IReadOnlyList<DiffLine> lines)
        {
            _renderer.Lines = lines;
            _numbers.Lines = lines;
            Editor.Text = string.Join("\n", lines.Select(line => line.Text));
            Editor.TextArea.TextView.Redraw();
        }

        public void Dispose() => TextMate.Dispose();

        // AXAML has no grammar of its own; it's XML.
        private static string? GrammarFor(RegistryOptions registryOptions, string filePath)
        {
            var extension = Path.GetExtension(filePath);
            var language = registryOptions.GetLanguageByExtension(extension)
                           ?? (extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase) ? registryOptions.GetLanguageByExtension(".xml") : null);
            return language is null ? null : registryOptions.GetScopeByLanguageId(language.Id);
        }
    }
}
