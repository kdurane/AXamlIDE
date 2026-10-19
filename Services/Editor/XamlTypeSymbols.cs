using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AXamlIDE.Services.Languages;
using AXamlIDE.Services.Lsp;
using AXamlIDE.Services.Xaml;

namespace AXamlIDE.Services.Editor;

/// <summary>
/// The C# source of a type named in AXAML (<see cref="XamlTypeReference"/>: x:DataType="vm:MainViewModel",
/// &lt;local:MyControl&gt;...), for F12. Asked of the C# language server; while that's still loading, found by file
/// name (MainViewModel.cs, MyControl.axaml.cs) the way you'd look for it yourself.
/// </summary>
public static class XamlTypeSymbols
{
    /// <summary>The type's declarations (its main file's for a partial class), or empty; the client when Roslyn found them.</summary>
    public static async Task<(LspClient? Client, IReadOnlyList<LspLocation> Declarations)> FindAsync(
        LanguageSessionService languages, XamlTypeReference type, Func<Task<IReadOnlyList<string>>> workspaceFiles)
    {
        if (await languages.ClientForLanguageAsync(LanguageIds.CSharp) is { SupportsWorkspaceSymbols: true } client)
        {
            // A type's container is its file ("MainViewModel.cs - MyApp (net10.0)"); members' start with "in ".
            var found = (await client.WorkspaceSymbolsAsync(type.Name))
                .Where(symbol => symbol.Name == type.Name && symbol.ContainerName?.StartsWith("in ", StringComparison.Ordinal) != true)
                .Where(symbol => !LspHelpers.IsSourceGenerated(symbol.Uri) && Uri.TryCreate(symbol.Uri, UriKind.Absolute, out var uri)
                                 && uri.IsFile && Declares(uri.LocalPath, type.ClrNamespace))
                .ToList();
            // A partial class: straight to its main file (NuGetDockable.cs, not NuGetDockable.Details.cs) when there is one.
            var main = found.Where(symbol => IsMainFile(new Uri(symbol.Uri).LocalPath, type.Name)).ToList();
            if (found.Count > 0)
                return (client, main.Count > 0 ? main : found);
        }

        var files = (await workspaceFiles())
            .Where(path => IsMainFile(path, type.Name))
            .Where(path => Declares(path, type.ClrNamespace))
            .Select(path => new LspLocation(new Uri(path).AbsoluteUri, ClassLine(path, type.Name)))
            .ToList();
        return (null, files);
    }

    private static bool IsMainFile(string path, string typeName)
        => Path.GetFileName(path) is var name && (name == typeName + ".cs" || name == typeName + ".axaml.cs");

    /// <summary>The file declares the namespace (file-scoped or block).</summary>
    private static bool Declares(string path, string ns)
    {
        try
        {
            return Regex.IsMatch(File.ReadAllText(path), $@"^\s*namespace\s+{Regex.Escape(ns)}\s*[;{{\r\n]", RegexOptions.Multiline);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The "class Name" line (the type's start for the editor to show), else the top of the file.</summary>
    private static LspRange ClassLine(string path, string name)
    {
        var lines = File.ReadAllLines(path);
        var declaration = new Regex($@"\b(class|record|struct|interface|enum)\s+{Regex.Escape(name)}\b");
        for (var i = 0; i < lines.Length; i++)
        {
            if (declaration.Match(lines[i]) is { Success: true } match)
            {
                var at = lines[i].IndexOf(name, match.Index, StringComparison.Ordinal);
                return new LspRange(i, at, i, at + name.Length);
            }
        }
        return new LspRange(0, 0, 0, 0);
    }
}
