using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using AXaxmlIDE.Models;
using AXaxmlIDE.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using TextMateSharp.Grammars;
// If EditorSettings / EditorSettingsStore / ThemeViewModel live in a namespace not listed above,
// copy the matching using from MainWindowViewModel.cs.

namespace AXaxmlIDE.ViewModels;

/// <summary>
/// Editor settings: fonts, line numbers, theme, explorer side, format-on-save.
/// Everything here was in MainWindowViewModel; property names are unchanged so the
/// settings window's bindings keep working.
///
/// It knows nothing about TextMate or the editor. Theme changes are announced through
/// <see cref="ThemeChanged"/> and the window applies them (this removed the view model's
/// last dependency on TextMate).
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly EditorSettingsStore _settingsStore;
    private EditorSettings _editorSettings;
    private ThemeViewModel _selectedTheme;

    public SettingsViewModel(EditorSettings settings, EditorSettingsStore settingsStore)
    {
        _editorSettings = settings;
        _settingsStore = settingsStore;

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
    }

    public ObservableCollection<ThemeViewModel> AvailableThemes { get; } =
    [
        .. Enum.GetValues<ThemeName>().Select(ThemeViewModel.BuiltIn),
        ThemeViewModel.Custom("AxamlDark", "AXAML Dark", "avares://AXaxmlIDE/Assets/axaml-dark.json"),
    ];

    public ObservableCollection<string> AvailableEditorFontFamilies { get; }

    public ObservableCollection<double> AvailableEditorFontSizes { get; } =
        [.. Enumerable.Range(6, 67).Select(size => (double)size)];

    public event Action? EditorSettingsChanged;
    public event Action<bool>? SolutionExplorerSideChanged;
    public event Action<ThemeViewModel>? ThemeChanged;

    /// <summary>"Couldn't save editor settings" etc. MainWindow forwards this to the status bar.</summary>
    public event Action<string>? StatusMessageChanged;

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

    public ThemeViewModel SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value)) return;
            ThemeChanged?.Invoke(value);
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

    private void UpdateEditorSettings(EditorSettings settings)
    {
        var explorerSideChanged = _editorSettings.SolutionExplorerOnRight != settings.SolutionExplorerOnRight;
        _editorSettings = settings;
        OnPropertyChanged(nameof(EditorFontFamily));
        OnPropertyChanged(nameof(EditorFontSize));
        OnPropertyChanged(nameof(ShowLineNumbers));
        OnPropertyChanged(nameof(HighlightCurrentLine));
        OnPropertyChanged(nameof(SolutionExplorerOnRight));
        OnPropertyChanged(nameof(FormatOnSave));
        EditorSettingsChanged?.Invoke();
        if (explorerSideChanged)
            SolutionExplorerSideChanged?.Invoke(settings.SolutionExplorerOnRight);

        try
        {
            _settingsStore.Save(_editorSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessageChanged?.Invoke($"Couldn't save editor settings: {ex.Message}");
        }
    }
}
