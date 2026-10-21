using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AXamlIDE.Services.CSharp;

namespace AXamlIDE.Services.Workspace;

/// <summary>One file's text before and after a namespace update, and what changed in it (for the confirmation).</summary>
public sealed record NamespaceFileUpdate(string Path, string OriginalText, string UpdatedText, IReadOnlyList<string> Changes);

/// <summary>
/// Rider's "update namespaces" for a file or folder moved in the explorer. Worked out before the move (so the
/// old folders still exist), applied after it. <see cref="NamespaceFileUpdate.Path"/> is where each file will be
/// once moved.
///
/// What it changes:
///  - In the moved files: the namespace line and x:Class, but only where they matched the old folder (a file
///    with a namespace of its own choosing is left alone). A moved file that still uses something left behind in
///    its old namespace gets a using for it.
///  - Everywhere else in the workspace: <c>Old.Ns.Type</c> becomes <c>New.Ns.Type</c>; a file that used a moved
///    type through <c>using Old.Ns;</c> (or by being in Old.Ns) gets <c>using New.Ns;</c>; and once nothing is
///    left in the old namespace, <c>using Old.Ns;</c> and <c>xmlns:x="using:Old.Ns"</c> point at the new one.
///
/// It reads the text, not the compiled code, so it finds a moved type by its name as a whole word. That can
/// add a using that isn't needed (harmless), which is why every change is shown before it's made.
/// </summary>
public static class NamespaceMove
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages"
    };

    /// <summary>
    /// The updates for moving <paramref name="source"/> (a file or folder) into <paramref name="targetFolder"/>.
    /// <paramref name="openTexts"/> holds open documents' text by full path (it wins over the file on disk).
    /// Empty when nothing needs to change.
    /// </summary>
    public static IReadOnlyList<NamespaceFileUpdate> Plan(
        string source, string targetFolder, string workspaceRoot, IReadOnlyDictionary<string, string> openTexts)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var sourceParent = Path.GetDirectoryName(source)!;
        var moving = Directory.Exists(source)
            ? CodeFiles(source).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal) { source, FileOperations.CodeBehindOf(source) ?? source };
        moving.RemoveWhere(path => !IsCodeFile(path));   // a moved image or text file has no namespace

        var files = new Dictionary<string, FileText>(StringComparer.Ordinal);
        foreach (var path in CodeFiles(workspaceRoot).Concat(moving).Distinct())
            if (Read(path, openTexts) is { } text)
                files[path] = new FileText(path, text, moving.Contains(path)
                    ? Path.Combine(targetFolder, Path.GetRelativePath(sourceParent, path))
                    : path);

        var moves = MovedFileChanges(files.Values.Where(f => f.IsMoving).ToList());
        if (moves.Count == 0)
            return [];

        var leftBehind = DeclaredTypes(files.Values.Where(f => !f.IsMoving));
        foreach (var move in moves)
            UseLeftBehindTypes(move, files.Values.Where(f => f.IsMoving), leftBehind);
        foreach (var move in moves)
        {
            var oldGone = !leftBehind.Keys.Any(ns => ns == move.OldNamespace || ns.StartsWith(move.OldNamespace + ".", StringComparison.Ordinal));
            foreach (var file in files.Values)
            {
                if (file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    UpdateCSharpReferences(file, move, oldGone);
                else
                    UpdateXamlReferences(file, move, oldGone);
            }
        }

        return [.. files.Values.Where(f => f.Edits.Count > 0).Select(f => f.ToUpdate()).OrderBy(u => u.Path, StringComparer.Ordinal)];
    }

    // ---------------------------------------------------------------- the moved files

    /// <summary>A namespace whose types moved, and the names of the types that went.</summary>
    private sealed record MovedNamespace(string OldNamespace, string NewNamespace, HashSet<string> Types);

    /// <summary>Change the moved files' namespace lines and x:Class, and say which types moved where.</summary>
    private static List<MovedNamespace> MovedFileChanges(List<FileText> moving)
    {
        var moves = new List<MovedNamespace>();
        foreach (var file in moving)
        {
            var oldNamespace = ItemTemplates.ProjectNamespaceFor(Path.GetDirectoryName(file.Path)!);
            var newNamespace = ItemTemplates.ProjectNamespaceFor(Path.GetDirectoryName(file.NewPath)!);
            if (oldNamespace is null || newNamespace is null || oldNamespace == newNamespace)
                continue;

            if (file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (CSharpText.Namespace(file.Text) is not { } declared || declared.Name != oldNamespace)
                    continue;   // not named after its folder: leave it as the author chose
                file.Replace(declared.Start, declared.Name.Length, newNamespace, $"namespace {oldNamespace} → {newNamespace}");
                var move = moves.FirstOrDefault(m => m.OldNamespace == oldNamespace && m.NewNamespace == newNamespace);
                if (move is null)
                    moves.Add(move = new MovedNamespace(oldNamespace, newNamespace, []));
                move.Types.UnionWith(CSharpText.DeclaredTypes(file.Text));
            }
            else if (XamlText.Class(file.Text) is { } xClass
                     && xClass.Name.StartsWith(oldNamespace + ".", StringComparison.Ordinal)
                     && !xClass.Name[(oldNamespace.Length + 1)..].Contains('.'))
            {
                file.Replace(xClass.Start, oldNamespace.Length, newNamespace,
                    $"x:Class {xClass.Name} → {newNamespace}{xClass.Name[oldNamespace.Length..]}");
            }
        }
        return moves;
    }

    /// <summary>A moved C# file that uses types left behind in its old namespace needs a using for it now.</summary>
    private static void UseLeftBehindTypes(MovedNamespace move, IEnumerable<FileText> moving, Dictionary<string, HashSet<string>> leftBehind)
    {
        if (!leftBehind.TryGetValue(move.OldNamespace, out var types))
            return;
        foreach (var file in moving)
        {
            if (!file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || CSharpText.Namespace(file.Text)?.Name != move.OldNamespace
                || CSharpText.HasUsing(file.Text, move.OldNamespace)
                || !types.Any(type => Words.Contains(file.Text, type)))
                continue;
            file.AddUsing(move.OldNamespace);
        }
    }

    // ---------------------------------------------------------------- everyone else

    private static void UpdateCSharpReferences(FileText file, MovedNamespace move, bool oldGone)
    {
        if (file.IsMoving && CSharpText.Namespace(file.Text)?.Name == move.OldNamespace)
            return;   // a moved file: done above

        // Old.Ns.Type → New.Ns.Type, wherever it's written out in full.
        foreach (var type in move.Types)
            foreach (var at in Words.FindQualified(file.Text, move.OldNamespace, type))
                file.Replace(at, move.OldNamespace.Length, move.NewNamespace, $"{move.OldNamespace}.{type} → {move.NewNamespace}.{type}");

        var fileNamespace = CSharpText.Namespace(file.Text)?.Name ?? "";
        var seesNew = CSharpText.HasUsing(file.Text, move.NewNamespace) || IsSameOrInside(fileNamespace, move.NewNamespace);
        var oldUsing = CSharpText.Using(file.Text, move.OldNamespace);

        if (oldUsing is not null && oldGone)
        {
            // Nothing is left in the old namespace, so its using would be an error now.
            if (seesNew || file.AddsUsing(move.NewNamespace))
                file.Replace(oldUsing.LineStart, oldUsing.LineLength, "", $"removed using {move.OldNamespace} (nothing is left in it)");
            else
                file.Replace(oldUsing.Start, move.OldNamespace.Length, move.NewNamespace, $"using {move.OldNamespace} → {move.NewNamespace}");
            return;
        }

        var seesOld = oldUsing is not null || IsSameOrInside(fileNamespace, move.OldNamespace);
        if (seesOld && !seesNew && move.Types.Any(type => Words.Contains(file.Text, type)))
            file.AddUsing(move.NewNamespace);
    }

    private static void UpdateXamlReferences(FileText file, MovedNamespace move, bool oldGone)
    {
        foreach (var xmlns in XamlText.Namespaces(file.Text).Where(x => x.Namespace == move.OldNamespace))
        {
            if (oldGone)
            {
                file.Replace(xmlns.NamespaceStart, move.OldNamespace.Length, move.NewNamespace,
                    $"xmlns:{xmlns.Prefix} → using:{move.NewNamespace}");
                continue;
            }

            // Some types stay in the old namespace: the moved ones get a prefix of their own.
            var uses = move.Types.SelectMany(type => Words.FindPrefixed(file.Text, xmlns.Prefix, type).Select(at => (at, type))).ToList();
            if (uses.Count == 0)
                continue;
            var prefix = XamlText.Namespaces(file.Text).FirstOrDefault(x => x.Namespace == move.NewNamespace)?.Prefix
                         ?? file.AddXmlns(xmlns, move.NewNamespace);
            foreach (var (at, type) in uses)
                file.Replace(at, xmlns.Prefix.Length, prefix, $"{xmlns.Prefix}:{type} → {prefix}:{type}");
        }
    }

    /// <summary>Code in namespace A.B.C can use A.B's types without a using.</summary>
    private static bool IsSameOrInside(string fileNamespace, string ns)
        => fileNamespace == ns || fileNamespace.StartsWith(ns + ".", StringComparison.Ordinal);

    /// <summary>The types each namespace declares, from the given C# files.</summary>
    private static Dictionary<string, HashSet<string>> DeclaredTypes(IEnumerable<FileText> files)
    {
        var byNamespace = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            if (CSharpText.Namespace(file.Text) is not { } ns)
                continue;
            if (!byNamespace.TryGetValue(ns.Name, out var types))
                byNamespace[ns.Name] = types = [];
            types.UnionWith(CSharpText.DeclaredTypes(file.Text));
        }
        return byNamespace;
    }

    // ---------------------------------------------------------------- files

    /// <summary>Every .cs and .axaml file under a folder, skipping bin, obj and hidden folders.</summary>
    private static IEnumerable<string> CodeFiles(string folder)
    {
        var folders = new Stack<string>([Path.GetFullPath(folder)]);
        while (folders.TryPop(out var current))
        {
            string[] here, below;
            try
            {
                here = Directory.GetFiles(current);
                below = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in here.Where(IsCodeFile))
                yield return file;
            foreach (var child in below)
            {
                var name = Path.GetFileName(child);
                if (!SkippedFolders.Contains(name) && !name.StartsWith('.'))
                    folders.Push(child);
            }
        }
    }

    private static bool IsCodeFile(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);

    private static string? Read(string path, IReadOnlyDictionary<string, string> openTexts)
    {
        if (openTexts.TryGetValue(path, out var open))
            return open;
        try
        {
            return new FileInfo(path).Length > 2 * 1024 * 1024 ? null : File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A file being looked at: its text, where it will be, and the edits collected for it so far.</summary>
    private sealed class FileText(string path, string text, string newPath)
    {
        private readonly HashSet<string> _addedUsings = [];

        public string Path { get; } = path;
        public string Text { get; } = text;
        public string NewPath { get; } = newPath;
        public bool IsMoving => Path != NewPath;
        public List<TextEdit> Edits { get; } = [];
        public List<string> Changes { get; } = [];

        public void Replace(int offset, int length, string replacement, string change)
        {
            if (Edits.Any(e => e.Offset < offset + length && offset < e.Offset + e.Length))
                return;   // something already changed this text
            Edits.Add(new TextEdit(offset, length, replacement));
            if (!Changes.Contains(change))
                Changes.Add(change);
        }

        /// <summary>Add <c>using ns;</c> (once), in order among the usings at the top.</summary>
        public void AddUsing(string ns)
        {
            if (!_addedUsings.Add(ns))
                return;
            var (offset, line) = CSharpText.PlaceForUsing(Text, ns);
            Edits.Add(new TextEdit(offset, 0, line));
            Changes.Add($"added using {ns}");
        }

        public bool AddsUsing(string ns) => _addedUsings.Contains(ns);

        /// <summary>Declare an xmlns for <paramref name="ns"/> just after the <paramref name="existing"/> one; returns its prefix.</summary>
        public string AddXmlns(XamlText.Xmlns existing, string ns)
        {
            var last = ns[(ns.LastIndexOf('.') + 1)..];
            var basePrefix = char.ToLowerInvariant(last[0]) + last[1..];
            var taken = XamlText.Namespaces(Text).Select(x => x.Prefix).ToHashSet();
            var prefix = basePrefix;
            for (var n = 2; taken.Contains(prefix); n++)
                prefix = basePrefix + n;
            // On a line of its own, lined up with the existing one, when that one starts its line.
            var lineStart = Text.LastIndexOf('\n', existing.Start) + 1;
            var indent = Text[lineStart..existing.Start];
            var separator = indent.All(char.IsWhiteSpace) ? (Text.Contains("\r\n") ? "\r\n" : "\n") + indent : " ";
            Edits.Add(new TextEdit(existing.End, 0, $"{separator}xmlns:{prefix}=\"using:{ns}\""));
            Changes.Add($"added xmlns:{prefix}=\"using:{ns}\"");
            return prefix;
        }

        public NamespaceFileUpdate ToUpdate()
        {
            var text = Text;
            foreach (var edit in Edits.OrderByDescending(e => e.Offset).ThenByDescending(e => e.Length))
                text = text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.Replacement);
            return new NamespaceFileUpdate(NewPath, Text, text, Changes);
        }
    }
}

/// <summary>Finding namespaces, usings and type declarations in C# text, line by line (no parser, no regex).</summary>
internal static class CSharpText
{
    /// <summary>A namespace declaration's name, and where the name starts.</summary>
    public sealed record NamespaceDeclaration(string Name, int Start);

    /// <summary>A using directive: where the namespace name starts, and the whole line (with its line break).</summary>
    public sealed record UsingDirective(string Namespace, int Start, int LineStart, int LineLength);

    /// <summary>The first <c>namespace X</c> (block or file-scoped).</summary>
    public static NamespaceDeclaration? Namespace(string text)
    {
        foreach (var (start, end) in Lines(text))
        {
            var i = SkipSpaces(text, start, end);
            if (!Words.At(text, i, "namespace") || i + 9 >= end || !char.IsWhiteSpace(text[i + 9]))
                continue;
            var nameStart = SkipSpaces(text, i + 9, end);
            var nameEnd = nameStart;
            while (nameEnd < end && (Words.IsIdentifierChar(text[nameEnd]) || text[nameEnd] == '.'))
                nameEnd++;
            if (nameEnd > nameStart)
                return new NamespaceDeclaration(text[nameStart..nameEnd], nameStart);
        }
        return null;
    }

    public static bool HasUsing(string text, string ns) => Using(text, ns) is not null;

    /// <summary><c>using ns;</c> or <c>global using ns;</c> in this file (not aliases or using static).</summary>
    public static UsingDirective? Using(string text, string ns)
        => Usings(text).FirstOrDefault(u => u.Namespace == ns);

    private static IEnumerable<UsingDirective> Usings(string text)
    {
        foreach (var (start, end) in Lines(text))
        {
            var i = SkipSpaces(text, start, end);
            if (Words.At(text, i, "global"))
                i = SkipSpaces(text, i + 6, end);
            if (!Words.At(text, i, "using") || i + 5 >= end || !char.IsWhiteSpace(text[i + 5]))
                continue;
            var nameStart = SkipSpaces(text, i + 5, end);
            var semicolon = text.IndexOf(';', nameStart, end - nameStart);
            if (semicolon < 0)
                continue;
            var name = text[nameStart..semicolon].Trim();
            if (name.Length == 0 || name.Contains('=') || name.Contains(' ') || name.Contains('('))
                continue;   // an alias, using static, or a using statement
            var lineEnd = end < text.Length && text[end] == '\r' ? end + 1 : end;
            if (lineEnd < text.Length && text[lineEnd] == '\n')
                lineEnd++;
            yield return new UsingDirective(name, nameStart, start, lineEnd - start);
        }
    }

    /// <summary>Where to insert <c>using ns;</c>: alphabetically among the file's usings, else above the namespace.</summary>
    public static (int Offset, string Text) PlaceForUsing(string text, string ns)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var usings = Usings(text).Where(u => !text.AsSpan(u.LineStart, u.LineLength).TrimStart().StartsWith("global")).ToList();
        if (usings.Count > 0)
        {
            var after = usings.FirstOrDefault(u => string.CompareOrdinal(u.Namespace, ns) > 0);
            if (after is not null)
                return (after.LineStart, $"using {ns};{newline}");
            var last = usings[^1];
            var end = last.LineStart + last.LineLength;
            return end > 0 && text[end - 1] == '\n' ? (end, $"using {ns};{newline}") : (end, $"{newline}using {ns};");
        }
        if (Namespace(text) is { } declaration)
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(declaration.Start - 1, 0)) + 1;
            return (lineStart, $"using {ns};{newline}{newline}");
        }
        return (0, $"using {ns};{newline}{newline}");
    }

    /// <summary>The names after class, record, struct, interface and enum.</summary>
    public static IEnumerable<string> DeclaredTypes(string text)
    {
        foreach (var keyword in (string[])["class", "record", "struct", "interface", "enum"])
        {
            foreach (var at in Words.Find(text, keyword))
            {
                var i = at + keyword.Length;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                var start = i;
                while (i < text.Length && Words.IsIdentifierChar(text[i]))
                    i++;
                var name = text[start..i];
                if (name.Length > 0 && name is not ("class" or "struct") && !char.IsDigit(name[0]))
                    yield return name;
            }
        }
    }

    private static int SkipSpaces(string text, int i, int end)
    {
        while (i < end && (text[i] == ' ' || text[i] == '\t'))
            i++;
        return i;
    }

    /// <summary>Each line's start and end (before "\r\n" or "\n").</summary>
    private static IEnumerable<(int Start, int End)> Lines(string text)
    {
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            yield return (start, end > start && text[end - 1] == '\r' ? end - 1 : end);
            if (newline < 0)
                yield break;
            start = newline + 1;
        }
    }
}

/// <summary>Finding x:Class and xmlns declarations in AXAML text.</summary>
internal static class XamlText
{
    public sealed record ClassName(string Name, int Start);

    /// <summary>An xmlns declaration pointing at a CLR namespace, from its "xmlns:" (Start) to just after its closing quote (End).</summary>
    public sealed record Xmlns(string Prefix, string Namespace, int Start, int NamespaceStart, int End);

    public static ClassName? Class(string text)
    {
        var at = text.IndexOf("x:Class=\"", StringComparison.Ordinal);
        if (at < 0)
            return null;
        var start = at + 9;
        var end = text.IndexOf('"', start);
        return end < 0 ? null : new ClassName(text[start..end], start);
    }

    /// <summary>Every <c>xmlns:prefix="using:Ns"</c> (and <c>clr-namespace:Ns;assembly=...</c>).</summary>
    public static IEnumerable<Xmlns> Namespaces(string text)
    {
        var at = 0;
        while ((at = text.IndexOf("xmlns:", at, StringComparison.Ordinal)) >= 0)
        {
            var start = at;
            var prefixStart = at + 6;
            at = prefixStart;
            var equals = text.IndexOf('=', prefixStart);
            if (equals < 0)
                yield break;
            var prefix = text[prefixStart..equals].Trim();
            if (equals + 1 >= text.Length || text[equals + 1] is not ('"' or '\''))
                continue;
            var quote = text[equals + 1];
            var valueStart = equals + 2;
            var valueEnd = text.IndexOf(quote, valueStart);
            if (valueEnd < 0)
                yield break;

            foreach (var scheme in (string[])["using:", "clr-namespace:"])
            {
                if (string.CompareOrdinal(text, valueStart, scheme, 0, scheme.Length) != 0)
                    continue;
                var nsStart = valueStart + scheme.Length;
                var nsEnd = text.IndexOf(';', nsStart, valueEnd - nsStart);
                yield return new Xmlns(prefix, text[nsStart..(nsEnd < 0 ? valueEnd : nsEnd)], start, nsStart, valueEnd + 1);
            }
        }
    }
}

/// <summary>Whole-word searches: "View" in "MainView" or "Views" isn't a match.</summary>
internal static class Words
{
    public static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static bool At(string text, int offset, string word)
        => offset >= 0 && offset + word.Length <= text.Length
           && string.CompareOrdinal(text, offset, word, 0, word.Length) == 0
           && (offset + word.Length == text.Length || !IsIdentifierChar(text[offset + word.Length]))
           && (offset == 0 || !IsIdentifierChar(text[offset - 1]));

    public static IEnumerable<int> Find(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal))
            if (At(text, at, word))
                yield return at;
    }

    public static bool Contains(string text, string word) => Find(text, word).Any();

    /// <summary>Where <c>ns.Type</c> is written in full (the offset of ns), not as part of a longer name.</summary>
    public static IEnumerable<int> FindQualified(string text, string ns, string type)
        => Find(text, type)
            .Where(at => at > ns.Length && text[at - 1] == '.'
                         && string.CompareOrdinal(text, at - 1 - ns.Length, ns, 0, ns.Length) == 0
                         && (at - 1 - ns.Length == 0 || !(IsIdentifierChar(text[at - 2 - ns.Length]) || text[at - 2 - ns.Length] == '.')))
            .Select(at => at - 1 - ns.Length);

    /// <summary>Where <c>prefix:Type</c> appears in XAML (the offset of prefix).</summary>
    public static IEnumerable<int> FindPrefixed(string text, string prefix, string type)
        => Find(text, type)
            .Where(at => at > prefix.Length && text[at - 1] == ':'
                         && string.CompareOrdinal(text, at - 1 - prefix.Length, prefix, 0, prefix.Length) == 0
                         && (at - 1 - prefix.Length == 0 || !(IsIdentifierChar(text[at - 2 - prefix.Length]) || text[at - 2 - prefix.Length] == ':')))
            .Select(at => at - 1 - prefix.Length);
}
