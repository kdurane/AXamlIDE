using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AXamlIDE.Services.NuGet;

/// <summary>
/// Adds, updates and removes &lt;PackageReference&gt; elements by editing the project's text, so the rest of the file
/// stays exactly as written (dotnet add package re-saves the whole project through MSBuild, which puts every
/// element's attributes on one line). Each method returns the new text, or null when the reference isn't written
/// in a simple way (Version as a child element or a $(Property), ...): then use dotnet add / remove instead.
/// </summary>
public static partial class PackageReferenceEditor
{
    // An opening tag (possibly self-closing, possibly over several lines) of a PackageReference.
    [GeneratedRegex(@"<PackageReference\b(?<attributes>[^>]*?)(?<selfClosing>/?)>")]
    private static partial Regex OpeningTag();

    [GeneratedRegex(@"\bInclude\s*=\s*""(?<id>[^""]*)""")]
    private static partial Regex IncludeAttribute();

    [GeneratedRegex(@"\bVersion\s*=\s*""(?<version>[^""]*)""")]
    private static partial Regex VersionAttribute();

    [GeneratedRegex(@"</PackageReference\s*>")]
    private static partial Regex ClosingTag();

    [GeneratedRegex(@"^(?<indent>[ \t]*)<(?!\?|!)", RegexOptions.Multiline)]
    private static partial Regex IndentedElement();

    /// <summary>Add a reference, or change its version if the project already has one.</summary>
    public static string? AddOrUpdate(string project, string id, string version)
    {
        if (Find(project, id) is { } existing)
            return SetVersion(project, existing, version);

        var newline = project.Contains("\r\n") ? "\r\n" : "\n";
        var element = $"<PackageReference Include=\"{id}\" Version=\"{version}\" />";

        // After the last reference, at its indentation.
        var references = OpeningTag().Matches(project);
        if (references.Count > 0)
        {
            var last = references[^1];
            var end = EndOf(project, last);
            if (end < 0)
                return null;
            return project[..end] + newline + IndentOf(project, last.Index) + element + project[end..];
        }

        // No references yet: a new ItemGroup at the end, indented like the project's other elements.
        var close = project.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (close < 0)
            return null;
        var indent = IndentedElement().Matches(project).Select(match => match.Groups["indent"].Value).Where(i => i.Length > 0)
            .OrderBy(i => i.Length).FirstOrDefault() ?? "  ";
        var lineStart = project.LastIndexOf('\n', close - 1) + 1;
        var group = $"{indent}<ItemGroup>{newline}{indent}{indent}{element}{newline}{indent}</ItemGroup>{newline}";
        var before = project[..lineStart].TrimEnd(' ', '\t');
        var blankLine = before.EndsWith(newline + newline) || before.Length == 0 ? "" : newline;
        return before + blankLine + group + project[lineStart..];
    }

    /// <summary>Remove a reference (with its line, and its ItemGroup if that's left empty).</summary>
    public static string? Remove(string project, string id)
    {
        if (Find(project, id) is not { } existing)
            return null;
        var end = EndOf(project, existing);
        if (end < 0)
            return null;

        // Take the whole line(s) when nothing else is on them.
        var start = existing.Index;
        var lineStart = project.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var lineEnd = project.IndexOf('\n', end);
        lineEnd = lineEnd < 0 ? project.Length : lineEnd + 1;
        if (project[lineStart..start].Trim().Length == 0 && project[end..lineEnd].Trim().Length == 0)
            (start, end) = (lineStart, lineEnd);
        var result = project[..start] + project[end..];

        return RemoveEmptyItemGroup(result, start);
    }

    /// <summary>The version a reference asks for, or null (not referenced, or not written as Version="...").</summary>
    public static string? VersionOf(string project, string id)
        => Find(project, id) is { } tag && VersionAttribute().Match(tag.Groups["attributes"].Value) is { Success: true } version
            ? version.Groups["version"].Value
            : null;

    private static Match? Find(string project, string id)
        => OpeningTag().Matches(project).FirstOrDefault(tag =>
            IncludeAttribute().Match(tag.Groups["attributes"].Value) is { Success: true } include
            && string.Equals(include.Groups["id"].Value.Trim(), id, StringComparison.OrdinalIgnoreCase));

    private static string? SetVersion(string project, Match tag, string version)
    {
        var attributes = tag.Groups["attributes"];
        var current = VersionAttribute().Match(attributes.Value);
        if (!current.Success || current.Groups["version"].Value.Contains("$("))
            return null;   // a child <Version>, central package management, or a property: leave it to dotnet
        var value = current.Groups["version"];
        var at = attributes.Index + value.Index;
        return project[..at] + version + project[(at + value.Length)..];
    }

    /// <summary>Where the element ends: after "/>", or after its closing tag. -1 if it has no closing tag.</summary>
    private static int EndOf(string project, Match tag)
    {
        var afterTag = tag.Index + tag.Length;
        if (tag.Groups["selfClosing"].Value == "/")
            return afterTag;
        var close = ClosingTag().Match(project, afterTag);
        return close.Success ? close.Index + close.Length : -1;
    }

    private static string IndentOf(string project, int index)
    {
        var lineStart = project.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var indent = project[lineStart..index];
        return indent.Trim().Length == 0 ? indent : "";
    }

    /// <summary>If the edit at <paramref name="at"/> left its ItemGroup with nothing in it, remove the group too.</summary>
    private static string RemoveEmptyItemGroup(string project, int at)
    {
        var open = project.LastIndexOf("<ItemGroup", Math.Min(at, project.Length - 1), StringComparison.Ordinal);
        if (open < 0)
            return project;
        var openEnd = project.IndexOf('>', open);
        var close = project.IndexOf("</ItemGroup>", openEnd, StringComparison.Ordinal);
        if (openEnd < 0 || close < 0 || close < at || project[open..openEnd].EndsWith('/')
            || project[(openEnd + 1)..close].Trim().Length > 0)
            return project;

        var start = project.LastIndexOf('\n', Math.Max(0, open - 1)) + 1;
        var end = project.IndexOf('\n', close);
        end = end < 0 ? project.Length : end + 1;
        if (project[start..open].Trim().Length > 0)
            start = open;
        var result = project[..start] + project[end..];

        // Don't leave a blank line where the group was: above the parent's closing tag, or next to another blank line.
        var newline = project.Contains("\r\n") ? "\r\n" : "\n";
        var blankAbove = result[..start].EndsWith(newline + newline, StringComparison.Ordinal);
        var rest = result[start..];
        var next = rest.TrimStart(' ', '\t');
        if (blankAbove && (next.StartsWith("</", StringComparison.Ordinal) || next.StartsWith(newline, StringComparison.Ordinal)))
            result = result.Remove(start - newline.Length, newline.Length);
        return result;
    }
}
