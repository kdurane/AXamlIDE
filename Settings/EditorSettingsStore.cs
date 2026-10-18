using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TextMateSharp.Grammars;

namespace AXamlIDE.Settings;

public sealed record EditorSettingsLoadResult(EditorSettings Settings, IReadOnlyList<string> Warnings);

public sealed class EditorSettingsStore(string? settingsPath = null)
{
    private const int CurrentSchemaVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath { get; } = settingsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AXamlIDE",
        "settings.json");

    public EditorSettingsLoadResult Load()
    {
        string json;
        try
        {
            json = File.ReadAllText(SettingsPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new EditorSettingsLoadResult(new EditorSettings(), []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EditorSettingsLoadResult(new EditorSettings(),
                [$"Couldn't read editor settings at '{SettingsPath}': {ex.Message}"]);
        }

        PersistedEditorSettings? persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<PersistedEditorSettings>(json);
        }
        catch (JsonException ex)
        {
            return new EditorSettingsLoadResult(new EditorSettings(),
                [$"Editor settings at '{SettingsPath}' contain invalid JSON: {ex.Message}"]);
        }

        if (persisted is null)
            return new EditorSettingsLoadResult(new EditorSettings(),
                [$"Editor settings at '{SettingsPath}' are empty or invalid; defaults are being used."]);

        if (persisted.SchemaVersion is < 1 or > CurrentSchemaVersion)
            return new EditorSettingsLoadResult(new EditorSettings(),
                [$"Editor settings version {persisted.SchemaVersion} is not supported; defaults are being used."]);

        var warnings = new List<string>();
        var defaults = new EditorSettings();
        var themeId = defaults.ThemeId;
        if (!string.IsNullOrWhiteSpace(persisted.Theme))
        {
            if (ThemeIds.Normalize(persisted.Theme) is { } known)
                themeId = known;
            else
                warnings.Add($"Unknown editor theme '{persisted.Theme}'; the default theme is being used.");
        }

        var fontFamily = defaults.FontFamily;
        if (persisted.FontFamily is not null)
        {
            if (IsValidFontFamily(persisted.FontFamily))
                fontFamily = persisted.FontFamily.Trim();
            else
                warnings.Add("The saved editor font family is invalid; the default is being used.");
        }

        var fontSize = defaults.FontSize;
        if (persisted.FontSize is not { } savedFontSize)
            return new EditorSettingsLoadResult(new EditorSettings
            {
                ThemeId = themeId,
                FontFamily = fontFamily,
                FontSize = fontSize,
                ShowLineNumbers = persisted.ShowLineNumbers ?? defaults.ShowLineNumbers,
                HighlightCurrentLine = persisted.HighlightCurrentLine ?? defaults.HighlightCurrentLine,
                SolutionExplorerOnRight = persisted.SolutionExplorerOnRight ?? defaults.SolutionExplorerOnRight,
                FormatOnSave = persisted.FormatOnSave ?? defaults.FormatOnSave
            }, warnings);
        if (double.IsFinite(savedFontSize) && savedFontSize is >= 6 and <= 72)
            fontSize = savedFontSize;
        else
            warnings.Add("The saved editor font size must be between 6 and 72; the default is being used.");

        return new EditorSettingsLoadResult(new EditorSettings
        {
            ThemeId = themeId,
            FontFamily = fontFamily,
            FontSize = fontSize,
            ShowLineNumbers = persisted.ShowLineNumbers ?? defaults.ShowLineNumbers,
            HighlightCurrentLine = persisted.HighlightCurrentLine ?? defaults.HighlightCurrentLine,
            SolutionExplorerOnRight = persisted.SolutionExplorerOnRight ?? defaults.SolutionExplorerOnRight,
            FormatOnSave = persisted.FormatOnSave ?? defaults.FormatOnSave
        }, warnings);
    }

    public void Save(EditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValidFontFamily(settings.FontFamily))
            throw new ArgumentException("Editor font family must contain 1 to 256 non-control characters.", nameof(settings));
        if (!double.IsFinite(settings.FontSize) || settings.FontSize is < 6 or > 72)
            throw new ArgumentOutOfRangeException(nameof(settings), "Editor font size must be between 6 and 72.");
        if (!ThemeIds.IsKnown(settings.ThemeId!))
            throw new ArgumentOutOfRangeException(nameof(settings), "Editor theme is not supported.");

        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The editor settings path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var persisted = new PersistedEditorSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            Theme = settings.ThemeId,
            FontFamily = settings.FontFamily,
            FontSize = settings.FontSize,
            ShowLineNumbers = settings.ShowLineNumbers,
            HighlightCurrentLine = settings.HighlightCurrentLine,
            SolutionExplorerOnRight = settings.SolutionExplorerOnRight,
            FormatOnSave = settings.FormatOnSave
        };
        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(persisted, JsonOptions));
        File.Move(tempPath, SettingsPath, overwrite: true);
    }

    private static bool IsValidFontFamily(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 256
           && !value.Any(char.IsControl);

    private sealed class PersistedEditorSettings
    {
        public int SchemaVersion { get; init; }
        public string? Theme { get; init; }
        public string? FontFamily { get; init; }
        public double? FontSize { get; init; }
        public bool? ShowLineNumbers { get; init; }
        public bool? HighlightCurrentLine { get; init; }
        public bool? SolutionExplorerOnRight { get; init; }
        public bool? FormatOnSave { get; init; }
    }
}
