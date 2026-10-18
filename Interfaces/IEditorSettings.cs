using System.ComponentModel;

namespace AXamlIDE.Interfaces;

/// <summary>What the editor reads from settings. SettingsViewModel implements it.</summary>
public interface IEditorSettings : INotifyPropertyChanged
{
    string EditorFontFamily { get; }
    double EditorFontSize { get; set; }   // change to int if that's what you have
    bool ShowLineNumbers { get; }
    bool HighlightCurrentLine { get; }
}