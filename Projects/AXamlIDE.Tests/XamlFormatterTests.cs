using System;
using System.IO;
using System.Linq;
using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Tests;

public sealed class XamlFormatterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("axamlide-format-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string FileIn(string relativeFolder)
    {
        var folder = Path.Combine(_folder, relativeFolder);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "View.axaml");
    }

    private const string Messy =
        "<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
        "<StackPanel>\n" +
        "        <Button Margin=\"4\" x:Name=\"Ok\" Content=\"OK\"></Button>\n" +
        "</StackPanel>\n" +
        "</UserControl>\n";

    [Fact]
    public void Formats_with_the_default_settings()
    {
        var formatted = XamlFormatter.Format(Messy, FileIn("a"), _folder);

        Assert.Equal(
            "<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "    <StackPanel>\n" +
            "        <Button\n" +
            "            x:Name=\"Ok\"\n" +
            "            Margin=\"4\"\n" +
            "            Content=\"OK\" />\n" +
            "    </StackPanel>\n" +
            "</UserControl>\n",
            formatted);
    }

    [Fact]
    public void Formatting_twice_changes_nothing()
    {
        var once = XamlFormatter.Format(Messy, FileIn("a"), _folder);
        Assert.Equal(once, XamlFormatter.Format(once, FileIn("a"), _folder));
    }

    [Fact]
    public void Keeps_the_files_line_endings()
    {
        var formatted = XamlFormatter.Format(Messy.ReplaceLineEndings("\r\n"), FileIn("a"), _folder);

        Assert.Contains("\r\n", formatted);
        Assert.DoesNotContain("\n", formatted.Replace("\r\n", ""));
        Assert.EndsWith("</UserControl>\r\n", formatted);
    }

    [Fact]
    public void Uses_the_nearest_settings_file_up_to_the_workspace()
    {
        var file = FileIn(Path.Combine("src", "Views"));
        File.WriteAllText(Path.Combine(_folder, "src", XamlFormatter.SettingsFileName),
            """
            {
              // Settings.XamlStyler files often have comments
              "IndentSize": 2,
              "AttributesTolerance": "5",
            }
            """);

        var formatted = XamlFormatter.Format(Messy, file, _folder);

        Assert.Contains("\n  <StackPanel>", formatted);
        Assert.Contains("<Button x:Name=\"Ok\" Margin=\"4\" Content=\"OK\" />", formatted);
    }

    [Fact]
    public void Uses_the_ides_indent_size_without_a_settings_file()
        => Assert.Contains("\n  <StackPanel>", XamlFormatter.Format(Messy, FileIn("a"), _folder, indentSize: 2));

    [Fact]
    public void A_settings_file_indent_size_wins_over_the_ides()
    {
        File.WriteAllText(Path.Combine(_folder, XamlFormatter.SettingsFileName), """{ "IndentSize": 3 }""");

        Assert.Contains("\n   <StackPanel>", XamlFormatter.Format(Messy, FileIn("a"), _folder, indentSize: 8));
    }

    [Fact]
    public void A_settings_file_without_an_indent_size_keeps_the_ides()
    {
        File.WriteAllText(Path.Combine(_folder, XamlFormatter.SettingsFileName), """{ "AttributesTolerance": 1 }""");

        Assert.Contains("\n  <StackPanel>", XamlFormatter.Format(Messy, FileIn("a"), _folder, indentSize: 2));
    }

    [Fact]
    public void Does_not_look_above_the_workspace()
    {
        var workspace = Path.Combine(_folder, "solution");
        File.WriteAllText(Path.Combine(_folder, XamlFormatter.SettingsFileName), """{ "IndentSize": 2 }""");

        Assert.Null(XamlFormatter.FindSettings(FileIn(Path.Combine("solution", "Views")), workspace));
        Assert.NotNull(XamlFormatter.FindSettings(FileIn(Path.Combine("solution", "Views")), workspaceRoot: null));
    }

    [Fact]
    public void Broken_xaml_throws()
        => Assert.ThrowsAny<System.Xml.XmlException>(() => XamlFormatter.Format("<UserControl><Button></UserControl>", FileIn("a"), _folder));

    [Theory]
    [InlineData("a\nb\nc\n", "a\nB\nc\n")]
    [InlineData("a\nb\nc\n", "a\nb\nb2\nc\n")]
    [InlineData("a\nb\nc\n", "a\nc\n")]
    [InlineData("a\r\nb\r\nc", "x\r\nb\r\nc\r\nd")]
    [InlineData("", "a\n")]
    [InlineData("same\n", "same\n")]
    public void Line_edits_turn_the_old_text_into_the_new(string oldText, string newText)
    {
        var edits = XamlFormatter.LineEdits(oldText, newText);

        var result = oldText;
        foreach (var edit in edits.Reverse())
            result = result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
        Assert.Equal(newText, result);
    }

    [Fact]
    public void Line_edits_leave_unchanged_lines_alone()
    {
        var edits = XamlFormatter.LineEdits("one\ntwo\nthree\n", "one\nTWO\nthree\n");

        var edit = Assert.Single(edits);
        Assert.Equal(new TextReplacement(4, 4, "TWO\n"), edit);
    }
}
