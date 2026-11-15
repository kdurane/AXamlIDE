using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AXamlIDE.XamlStyler;
using AXamlIDE.XamlStyler.Options;
using DiffPlex;
using DiffPlex.Chunkers;

namespace AXamlIDE.Services.Xaml;

/// <summary>One replacement in the original text: <paramref name="Length"/> characters at <paramref name="Start"/>.</summary>
public sealed record TextReplacement(int Start, int Length, string NewText);

/// <summary>
/// Formats AXAML/XAML with the vendored XAML Styler (see XamlStyler/UPSTREAM.md), using the project's
/// <c>Settings.XamlStyler</c> when there is one, so the result matches the VS and Rider extensions.
/// </summary>
public static class XamlFormatter
{
    /// <summary>The settings file the XAML Styler extensions look for.</summary>
    public const string SettingsFileName = "Settings.XamlStyler";

    /// <summary>
    /// The formatted text, keeping <paramref name="text"/>'s line endings and final newline.
    /// Throws <see cref="System.Xml.XmlException"/> when the XAML isn't well-formed.
    /// </summary>
    /// <param name="filePath">The file being formatted: the settings search starts in its folder.</param>
    /// <param name="workspaceRoot">Where the search stops (the solution folder); null searches up to the drive root.</param>
    /// <param name="indentSize">The IDE's indent size, used unless the settings file sets "IndentSize".</param>
    public static string Format(string text, string filePath, string? workspaceRoot, int indentSize = 4)
    {
        var settingsPath = FindSettings(filePath, workspaceRoot);
        var options = settingsPath is null ? new StylerOptions() : new StylerOptions(config: settingsPath);
        // As in the VS extension: the editor's indent size applies unless the settings file has its own.
        if (options.UseVisualStudioIndentSize)
            options.IndentSize = Math.Clamp(indentSize, 1, 16);
        var formatted = new StylerService(options, new XamlLanguageOptions { IsFormatable = true }).StyleDocument(text);

        // The styler writes Environment.NewLine everywhere and drops the final newline: keep the file's own.
        var newline = LineEndingOf(text);
        formatted = formatted.ReplaceLineEndings(newline);
        if (text.EndsWith('\n') && !formatted.EndsWith('\n'))
            formatted += newline;
        return formatted;
    }

    /// <summary>
    /// The nearest <see cref="SettingsFileName"/> from the file's folder upwards, stopping after
    /// <paramref name="workspaceRoot"/> when the file is inside it. Null when there isn't one.
    /// </summary>
    public static string? FindSettings(string filePath, string? workspaceRoot)
    {
        var root = workspaceRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        for (var folder = Path.GetDirectoryName(Path.GetFullPath(filePath)); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var candidate = Path.Combine(folder, SettingsFileName);
            if (File.Exists(candidate))
                return candidate;
            if (root is not null && string.Equals(folder, root, StringComparison.Ordinal))
                break;
        }
        return null;
    }

    /// <summary>
    /// The line-by-line replacements that turn <paramref name="oldText"/> into <paramref name="newText"/>, in
    /// document order. Applying few small edits (rather than replacing everything) keeps the caret, scroll
    /// position, breakpoints and markers on the lines that didn't change.
    /// </summary>
    public static IReadOnlyList<TextReplacement> LineEdits(string oldText, string newText)
    {
        // This chunker keeps each line's ending, so the old chunks add up to exactly oldText.
        var chunker = new LineEndingsPreservingChunker();
        var diff = Differ.Instance.CreateDiffs(oldText, newText, ignoreWhiteSpace: false, ignoreCase: false, chunker);

        var lineStarts = new int[diff.PiecesOld.Count + 1];
        for (var line = 0; line < diff.PiecesOld.Count; line++)
            lineStarts[line + 1] = lineStarts[line] + diff.PiecesOld[line].Length;

        var edits = new List<TextReplacement>(diff.DiffBlocks.Count);
        foreach (var block in diff.DiffBlocks)
        {
            var start = lineStarts[block.DeleteStartA];
            var length = lineStarts[block.DeleteStartA + block.DeleteCountA] - start;
            var inserted = string.Concat(diff.PiecesNew.Skip(block.InsertStartB).Take(block.InsertCountB));
            edits.Add(new TextReplacement(start, length, inserted));
        }
        return edits;
    }

    private static string LineEndingOf(string text)
    {
        var newline = text.IndexOf('\n');
        return newline > 0 && text[newline - 1] == '\r' ? "\r\n" : "\n";
    }
}
