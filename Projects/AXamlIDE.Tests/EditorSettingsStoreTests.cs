using System;
using System.IO;
using AXamlIDE.Models;
using AXamlIDE.Services.Settings;

namespace AXamlIDE.Tests;

public sealed class EditorSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "axamlide-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Missing_file_gives_defaults_without_warnings()
    {
        var result = new EditorSettingsStore(SettingsPath).Load();

        Assert.Equal(new EditorSettings(), result.Settings);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Saved_settings_load_back()
    {
        var store = new EditorSettingsStore(SettingsPath);
        var settings = new EditorSettings { FontSize = 17, ShowLineNumbers = false, SolutionExplorerOnRight = true, FormatOnSave = true };

        store.Save(settings);

        Assert.Equal(settings, new EditorSettingsStore(SettingsPath).Load().Settings);
    }

    [Fact]
    public void Invalid_json_gives_defaults_and_a_warning()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ not json");

        var result = new EditorSettingsStore(SettingsPath).Load();

        Assert.Equal(new EditorSettings(), result.Settings);
        Assert.Contains("invalid JSON", Assert.Single(result.Warnings));
    }
}
