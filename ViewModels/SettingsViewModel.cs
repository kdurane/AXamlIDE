using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AXamlIDE.Messages;
using AXamlIDE.Models;
using AXamlIDE.Services.Keymaps;
using AXamlIDE.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using TextMateSharp.Grammars;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The live editor settings (the Settings window binds to it, the editor reads it through
/// <see cref="IEditorSettings"/>). Every change is saved immediately and raises PropertyChanged
/// for exactly the properties that changed.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IEditorSettings
{
    private readonly EditorSettingsStore _settingsStore;
    private readonly IMessenger _messenger;
    private EditorSettings _editorSettings;
    private EditorTheme _selectedTheme;

    public SettingsViewModel(EditorSettingsLoadResult loaded, EditorSettingsStore settingsStore, IMessenger messenger)
    {
        var settings = loaded.Settings;
        _editorSettings = settings;
        _settingsStore = settingsStore;
        _messenger = messenger;

        if (!AvailableEditorFontSizes.Contains(settings.FontSize))
            AvailableEditorFontSizes.Add(settings.FontSize);
        AvailableEditorFontFamilies =
        [
            .. new[]
                  {
                      settings.FontFamily,
                      EditorSettings.DefaultFontFamily,
                      "Consolas,Menlo,Monospace",
                      "Menlo,Monospace",
                      "Monospace"
                  }.Distinct(StringComparer.Ordinal)
        ];
        _selectedTheme = AvailableThemes.FirstOrDefault(t => t.Id == settings.ThemeId)
                         ?? AvailableThemes.First(t => t.Id == nameof(ThemeName.DarkPlus));
        _selectedKeymap = KeymapLibrary.Get(settings.KeymapId);
        ShortcutRows = BuildShortcutRows();
    }

    public ObservableCollection<EditorTheme> AvailableThemes { get; } =
    [
        .. Enum.GetValues<ThemeName>().Select(EditorTheme.BuiltIn),
              EditorTheme.Custom(ThemeIds.AxamlDark, "AXAML Dark", "avares://AXamlIDE/Assets/axaml-dark.json"),
          ];

    public ObservableCollection<string> AvailableEditorFontFamilies { get; }

    public ObservableCollection<double> AvailableEditorFontSizes { get; } =
        [.. Enumerable.Range(6, 67).Select(size => (double)size)];

    public bool FormatOnSave
    {
        get => _editorSettings.FormatOnSave;
        set
        {
            if (_editorSettings.FormatOnSave == value)
                return;
            UpdateEditorSettings(_editorSettings with { FormatOnSave = value });
        }
    }

    public EditorTheme SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value)) return;
            UpdateEditorSettings(_editorSettings with { ThemeId = value.Id });
        }
    }

    public string EditorFontFamily
    {
        get => _editorSettings.FontFamily;
        set
        {
            if (string.Equals(_editorSettings.FontFamily, value, StringComparison.Ordinal))
                return;
            UpdateEditorSettings(_editorSettings with { FontFamily = value });
        }
    }

    public double EditorFontSize
    {
        get => _editorSettings.FontSize;
        set
        {
            if (Math.Abs(_editorSettings.FontSize - value) < 0.01)
                return;
            UpdateEditorSettings(_editorSettings with { FontSize = value });
        }
    }

    public bool ShowLineNumbers
    {
        get => _editorSettings.ShowLineNumbers;
        set
        {
            if (_editorSettings.ShowLineNumbers == value)
                return;
            UpdateEditorSettings(_editorSettings with { ShowLineNumbers = value });
        }
    }

    public bool HighlightCurrentLine
    {
        get => _editorSettings.HighlightCurrentLine;
        set
        {
            if (_editorSettings.HighlightCurrentLine == value)
                return;
            UpdateEditorSettings(_editorSettings with { HighlightCurrentLine = value });
        }
    }

    public bool SolutionExplorerOnRight
    {
        get => _editorSettings.SolutionExplorerOnRight;
        set
        {
            if (_editorSettings.SolutionExplorerOnRight == value)
                return;
            UpdateEditorSettings(_editorSettings with { SolutionExplorerOnRight = value });
        }
    }

    /// <summary>The bottom panel as it was left (DockLayoutViewModel keeps it up to date and restores it at startup).</summary>
    public PanelSettings Panel
    {
        get => _editorSettings.Panel;
        set
        {
            if (_editorSettings.Panel == value)
                return;
            UpdateEditorSettings(_editorSettings with { Panel = value });
        }
    }

    // ---------------------------------------------------------------- the helpers that can be switched off

    public EditorFeatureSettings Features => _editorSettings.Features;

    public bool AutoCloseBraces
    {
        get => Features.AutoCloseBraces;
        set => SetFeatures(Features with { AutoCloseBraces = value });
    }

    public bool AutoCloseXamlTags
    {
        get => Features.AutoCloseXamlTags;
        set => SetFeatures(Features with { AutoCloseXamlTags = value });
    }

    public bool FileScopedNamespaceOnSemicolon
    {
        get => Features.FileScopedNamespaceOnSemicolon;
        set => SetFeatures(Features with { FileScopedNamespaceOnSemicolon = value });
    }

    public bool CompletionWhileTyping
    {
        get => Features.CompletionWhileTyping;
        set => SetFeatures(Features with { CompletionWhileTyping = value });
    }

    public bool CompletionFromUnimportedNamespaces
    {
        get => Features.CompletionFromUnimportedNamespaces;
        set => SetFeatures(Features with { CompletionFromUnimportedNamespaces = value });
    }

    public bool ParameterInfoWhileTyping
    {
        get => Features.ParameterInfoWhileTyping;
        set => SetFeatures(Features with { ParameterInfoWhileTyping = value });
    }

    public bool QuickInfoOnHover
    {
        get => Features.QuickInfoOnHover;
        set => SetFeatures(Features with { QuickInfoOnHover = value });
    }

    public bool SemanticHighlighting
    {
        get => Features.SemanticHighlighting;
        set => SetFeatures(Features with { SemanticHighlighting = value });
    }

    public bool CodeLens
    {
        get => Features.CodeLens;
        set => SetFeatures(Features with { CodeLens = value });
    }

    public bool InlayParameterHints
    {
        get => Features.InlayParameterHints;
        set => SetFeatures(Features with { InlayParameterHints = value });
    }

    public bool InlayTypeHints
    {
        get => Features.InlayTypeHints;
        set => SetFeatures(Features with { InlayTypeHints = value });
    }

    public bool HighlightSelectionOccurrences
    {
        get => Features.HighlightSelectionOccurrences;
        set => SetFeatures(Features with { HighlightSelectionOccurrences = value });
    }

    public bool ColorSwatches
    {
        get => Features.ColorSwatches;
        set => SetFeatures(Features with { ColorSwatches = value });
    }

    public bool InlineDebugValues
    {
        get => Features.InlineDebugValues;
        set => SetFeatures(Features with { InlineDebugValues = value });
    }

    /// <summary>Change the features; PropertyChanged names the property set (for its checkbox) and <c>Features</c> (for the editor).</summary>
    private void SetFeatures(EditorFeatureSettings features, [CallerMemberName] string? propertyName = null)
    {
        if (features == _editorSettings.Features)
            return;
        UpdateEditorSettings(_editorSettings with { Features = features });
        OnPropertyChanged(propertyName);
    }

    private void UpdateEditorSettings(EditorSettings settings)
    {
        var old = _editorSettings;
        _editorSettings = settings;
        if (old.FontFamily != settings.FontFamily) OnPropertyChanged(nameof(EditorFontFamily));
        if (old.FontSize != settings.FontSize) OnPropertyChanged(nameof(EditorFontSize));
        if (old.ShowLineNumbers != settings.ShowLineNumbers) OnPropertyChanged(nameof(ShowLineNumbers));
        if (old.HighlightCurrentLine != settings.HighlightCurrentLine) OnPropertyChanged(nameof(HighlightCurrentLine));
        if (old.SolutionExplorerOnRight != settings.SolutionExplorerOnRight) OnPropertyChanged(nameof(SolutionExplorerOnRight));
        if (old.FormatOnSave != settings.FormatOnSave) OnPropertyChanged(nameof(FormatOnSave));
        if (old.Features != settings.Features) OnPropertyChanged(nameof(Features));

        try
        {
            _settingsStore.Save(_editorSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _messenger.Send(new StatusMessage($"Couldn't save editor settings: {ex.Message}"));
        }
    }
}
