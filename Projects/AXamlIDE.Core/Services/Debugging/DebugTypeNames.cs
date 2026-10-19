using System.Collections.Generic;
using System.Text;

namespace AXamlIDE.Services.Debugging;

/// <summary>Type names as the debug adapter gives them ("System.Collections.Generic.List&lt;MyApp.Person&gt;"), made readable.</summary>
public static class DebugTypeNames
{
    /// <summary>Without namespaces: "System.Collections.Generic.Dictionary&lt;string, MyApp.Person&gt;" -> "Dictionary&lt;string, Person&gt;".</summary>
    public static string Short(string type)
    {
        var result = new StringBuilder(type.Length);
        var segment = new StringBuilder();
        foreach (var c in type)
        {
            if (c is '<' or '>' or ',' or '[' or ']' or ' ' or '?')
            {
                result.Append(LastPart(segment.ToString())).Append(c);
                segment.Clear();
            }
            else
            {
                segment.Append(c);
            }
        }
        return result.Append(LastPart(segment.ToString())).ToString();

        static string LastPart(string part) => part.Contains('.') ? part[(part.LastIndexOf('.') + 1)..] : part;
    }

    /// <summary>
    /// What a collection holds, for "Count = 8,002 · List of Person": "Person" for List&lt;Person&gt; or Person[],
    /// "string → int" for a dictionary; null when it can't tell.
    /// </summary>
    public static string? ElementType(string type)
    {
        var shortName = Short(type);
        if (shortName.EndsWith(']'))
            return shortName[..shortName.IndexOf('[')];
        var open = shortName.IndexOf('<');
        if (open < 0 || !shortName.EndsWith('>'))
            return null;
        var arguments = SplitArguments(shortName[(open + 1)..^1]);
        return arguments.Count switch
        {
            1 => arguments[0],
            2 => $"{arguments[0]} → {arguments[1]}",   // Dictionary<TKey, TValue>
            _ => null
        };
    }

    /// <summary>"List&lt;Person&gt;" -> "List"; "Person[]" -> "array".</summary>
    public static string CollectionKind(string type)
    {
        var shortName = Short(type);
        if (shortName.EndsWith(']'))
            return "array";
        var open = shortName.IndexOf('<');
        return open < 0 ? shortName : shortName[..open];
    }

    /// <summary>Top-level generic arguments: "string, List&lt;int&gt;" -> ["string", "List&lt;int&gt;"].</summary>
    private static List<string> SplitArguments(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>') depth--;
            else if (text[i] == ',' && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(text[start..].Trim());
        return parts;
    }
}
