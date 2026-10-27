using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AXamlIDE.Services.Testing;

/// <summary>Where a test method is written: found by reading the project's .cs files for its class and method.</summary>
public static class TestSourceLocator
{
    /// <summary>The file, line (1-based) and column (0-based) of the method's name, or null if it isn't found.</summary>
    public static (string Path, int Line, int Column)? Find(string projectFolder, string className, string methodName)
    {
        foreach (var file in SourceFiles(projectFolder))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (FindInText(text, className, methodName) is { } found)
                return (file, found.Line, found.Column);
        }

        return null;
    }

    /// <summary>
    /// The method's declaration in this file's text: after "class ShortName" (when the class is known), the method
    /// name standing alone and followed by "(" or "&lt;". Line is 1-based, column 0-based.
    /// </summary>
    public static (int Line, int Column)? FindInText(string text, string className, string methodName)
    {
        var start = 0;
        var shortClass = ShortClassName(className);
        if (shortClass.Length > 0)
        {
            var classAt = FindWord(text, "class " + shortClass, 0);
            if (classAt < 0)
                return null;
            start = classAt;
        }

        for (var at = FindWord(text, methodName, start); at >= 0; at = FindWord(text, methodName, at + methodName.Length))
        {
            var after = at + methodName.Length;
            while (after < text.Length && text[after] is ' ' or '\t')
                after++;
            if (after < text.Length && text[after] is '(' or '<')
                return LineAndColumn(text, at);
        }

        return null;
    }

    /// <summary>"Ns.Outer+Inner`1" → "Inner".</summary>
    private static string ShortClassName(string className)
    {
        var name = className;
        var cut = name.LastIndexOfAny(['.', '+']);
        if (cut >= 0)
            name = name[(cut + 1)..];
        var arity = name.IndexOf('`');
        return arity >= 0 ? name[..arity] : name;
    }

    /// <summary>The next place <paramref name="word"/> appears as a whole word (not inside a longer name).</summary>
    private static int FindWord(string text, string word, int from)
    {
        for (var at = text.IndexOf(word, from, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 ? ' ' : text[at - 1];
            var end = at + word.Length;
            var next = end < text.Length ? text[end] : ' ';
            if (!IsNameCharacter(before) && !IsNameCharacter(next))
                return at;
        }

        return -1;
    }

    private static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static (int Line, int Column) LineAndColumn(string text, int offset)
    {
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] != '\n')
                continue;
            line++;
            lineStart = i + 1;
        }

        return (line, offset - lineStart);
    }

    private static IEnumerable<string> SourceFiles(string projectFolder)
    {
        if (!Directory.Exists(projectFolder))
            return [];
        return Directory.EnumerateFiles(projectFolder, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(projectFolder, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"));
    }
}
