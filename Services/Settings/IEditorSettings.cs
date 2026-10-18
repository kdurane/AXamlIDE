using System.ComponentModel;
using AXamlIDE.Models;

namespace AXamlIDE.Services.Settings;

/// <summary>What the editor reads from settings. SettingsViewModel implements it.</summary>
public interface IEditorSettings : INotifyPropertyChanged
{
    string EditorFontFamily { get; }
    double EditorFontSize { get; set; }   // settable: Ctrl+wheel in the editor changes it
    bool ShowLineNumbers { get; }
    bool HighlightCurrentLine { get; }
    EditorTheme SelectedTheme { get; }
    bool FormatOnSave { get; }
}
