using System;
using System.Text.RegularExpressions;

namespace AXamlIDE.Services.Xaml;

/// <summary>
/// A CLR type named with an xmlns prefix at a place in AXAML, for F12 to the type's C# source:
///   x:DataType="vm:MainViewModel", {x:Type vm:Item}, {x:Static vm:Strings.Title}, &lt;local:MyControl&gt;,
///   local:MyAttached.Value="...". The prefix is looked up in the document's xmlns declarations
///   ("using:My.App.ViewModels" or "clr-namespace:My.App.ViewModels;assembly=..."), so the full name is known
///   without the project being built. Avalonia's own types (URL namespaces) aren't references here.
/// </summary>
/// <param name="InTag">The name is an element's tag (&lt;local:MyControl&gt;), not inside an attribute.</param>
public sealed partial record XamlTypeReference(string Prefix, string Name, string ClrNamespace, bool InTag)
{
    public string FullName => ClrNamespace.Length == 0 ? Name : ClrNamespace + "." + Name;

    /// <summary>The prefixed type name around <paramref name="offset"/>, or null when there isn't one.</summary>
    public static XamlTypeReference? At(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var start = offset;
        while (start > 0 && IsTokenChar(text[start - 1]))
            start--;
        var end = offset;
        while (end < text.Length && IsTokenChar(text[end]))
            end++;

        var token = text[start..end];
        var colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1)
            return null;

        var prefix = token[..colon];
        var name = token[(colon + 1)..].Split('.')[0];   // vm:Strings.Title and local:Attached.Value name the type first
        if (name.Length == 0 || !char.IsLetter(name[0]) && name[0] != '_' || ClrNamespaceOf(text, prefix) is not { } ns)
            return null;
        return new XamlTypeReference(prefix, name, ns, start > 0 && text[start - 1] == '<');
    }

    /// <summary>The CLR namespace an xmlns prefix stands for in the document; null for URL namespaces.</summary>
    public static string? ClrNamespaceOf(string text, string prefix)
    {
        foreach (Match match in XmlnsDeclaration().Matches(text))
        {
            if (match.Groups["prefix"].Value != prefix)
                continue;
            var value = match.Groups["value"].Value.Trim();
            if (value.StartsWith("using:", StringComparison.Ordinal))
                return value["using:".Length..].Trim();
            if (value.StartsWith("clr-namespace:", StringComparison.Ordinal))
                return value["clr-namespace:".Length..].Split(';')[0].Trim();
            return null;
        }
        return null;
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.';

    [GeneratedRegex("""xmlns:(?<prefix>[\w.-]+)\s*=\s*["'](?<value>[^"']*)["']""")]
    private static partial Regex XmlnsDeclaration();
}
