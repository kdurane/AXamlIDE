using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace AXamlIDE.Services.Snippets;

/// <summary>A code snippet: typed as <see cref="Prefix"/> (then Tab, or picked from completion) it inserts <see cref="Body"/>.</summary>
public sealed record SnippetDefinition(string Name, string Prefix, string Description, string Body);

/// <summary>
/// The snippets for a language: the built-in ones (Services/Snippets/*.json, embedded) and the user's own, in VS
/// Code's snippet file format so snippets can be copied from VS Code as they are:
/// <code>{ "Property": { "prefix": "prop", "body": ["public ${1:int} ${2:Name} { get; set; }$0"], "description": "..." } }</code>
/// A user snippet replaces a built-in one with the same prefix. User files live in the settings folder
/// (~/.config/AXamlIDE/snippets/csharp.json) and are read again when they change.
/// </summary>
public sealed class SnippetLibrary(string userFolder)
{
    private readonly Dictionary<string, (DateTime Stamp, IReadOnlyList<SnippetDefinition> Snippets)> _user = [];
    private readonly Dictionary<string, IReadOnlyList<SnippetDefinition>> _builtIn = [];

    /// <summary>The default folder: snippets/ next to settings.json.</summary>
    public static string DefaultUserFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AXamlIDE", "snippets");

    public string UserFolder { get; } = userFolder;

    /// <summary>The languages with built-in snippets (LSP language ids, which name the files).</summary>
    public static IReadOnlyList<string> Languages { get; } = ["csharp", "axaml"];

    public string UserFilePath(string languageId) => Path.Combine(UserFolder, languageId + ".json");

    /// <summary>The language's snippets, user ones first and replacing built-in ones with the same prefix.</summary>
    public IReadOnlyList<SnippetDefinition> For(string languageId)
    {
        var user = UserSnippets(languageId);
        var prefixes = user.Select(snippet => snippet.Prefix).ToHashSet(StringComparer.Ordinal);
        return [.. user, .. BuiltIn(languageId).Where(snippet => !prefixes.Contains(snippet.Prefix))];
    }

    /// <summary>Creates the user's file for the language (with an example, commented out) if it isn't there.</summary>
    public string EnsureUserFile(string languageId)
    {
        var path = UserFilePath(languageId);
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(UserFolder);
        File.WriteAllText(path, $$"""
            {
                // Your {{languageId}} snippets, in VS Code's format (snippets copied from VS Code work as they are).
                // Type the prefix and press Tab, or pick it from the completion list. In the body: $1, $2... are the
                // fields Tab moves through, ${1:name} gives one a default, the same number twice types in both
                // places, $0 is where the caret ends, $TM_SELECTED_TEXT is the selection (Insert Snippet, Ctrl+J).
                // A snippet here replaces a built-in one with the same prefix.
                //
                // "Notify property": {
                //     "prefix": "propn",
                //     "body": [
                //         "private ${1:string} _${2:name};",
                //         "public $1 ${3:Name}",
                //         "{",
                //         "\tget => _$2;",
                //         "\tset => SetProperty(ref _$2, value);",
                //         "}$0"
                //     ],
                //     "description": "A property that raises PropertyChanged"
                // }
            }

            """);
        return path;
    }

    /// <summary>Snippets from a VS Code snippet file's text (comments and trailing commas allowed).</summary>
    public static IReadOnlyList<SnippetDefinition> Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var snippets = new List<SnippetDefinition>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return snippets;
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
                continue;
            var body = entry.Value.TryGetProperty("body", out var b) ? Lines(b) : [];
            var description = entry.Value.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";
            if (!entry.Value.TryGetProperty("prefix", out var p))
                continue;
            foreach (var prefix in Lines(p).Where(prefix => prefix.Length > 0))
                snippets.Add(new SnippetDefinition(entry.Name, prefix, description, string.Join("\n", body)));
        }
        return snippets;

        static string[] Lines(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString() ?? ""],
            JsonValueKind.Array => [.. value.EnumerateArray().Where(line => line.ValueKind == JsonValueKind.String).Select(line => line.GetString() ?? "")],
            _ => []
        };
    }

    private IReadOnlyList<SnippetDefinition> BuiltIn(string languageId)
    {
        if (_builtIn.TryGetValue(languageId, out var snippets))
            return snippets;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AXamlIDE.Snippets.{languageId}.json");
        snippets = stream is null ? [] : Parse(new StreamReader(stream).ReadToEnd());
        _builtIn[languageId] = snippets;
        return snippets;
    }

    private IReadOnlyList<SnippetDefinition> UserSnippets(string languageId)
    {
        var path = UserFilePath(languageId);
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);   // a fixed date in 1601 when there's no file
            if (_user.TryGetValue(languageId, out var cached) && cached.Stamp == stamp)
                return cached.Snippets;
            IReadOnlyList<SnippetDefinition> snippets = File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
            _user[languageId] = (stamp, snippets);
            return snippets;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];   // a half-written or broken file: just the built-in ones until it's fixed
        }
    }
}
