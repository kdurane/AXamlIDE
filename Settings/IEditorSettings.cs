using System.ComponentModel;

namespace AXamlIDE.Settings;

/// <summary>What the editor reads from settings. SettingsViewModel implements it.</summary>
public interface IEditorSettings : INotifyPropertyChanged
{
    string EditorFontFamily { get; }
    double EditorFontSize { get; set; }   // settable: Ctrl+wheel in the editor changes it
    bool ShowLineNumbers { get; }
    bool HighlightCurrentLine { get; }
    ThemeViewModel SelectedTheme { get; }
}
