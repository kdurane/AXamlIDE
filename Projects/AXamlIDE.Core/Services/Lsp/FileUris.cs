using System;
using System.IO;

namespace AXamlIDE.Services.Lsp;

/// <summary>
/// file:// URIs to local paths and back, the same on Windows, Linux and macOS. Language servers don't agree on how
/// to write a Windows drive: "file:///c:/src/a.cs", "file:///C:/src/a.cs" and VS Code's "file:///c%3A/src/a.cs"
/// all mean the same file. .NET's <see cref="Uri.LocalPath"/> only recognises the first two; given the third it
/// returns "/c:/src/a.cs", which <see cref="Path.GetFullPath(string)"/> then turns into "C:\c:\src\a.cs". So every
/// URI from a server goes through <see cref="ToPath(string?)"/>, and URIs are compared with <see cref="SameFile"/>.
/// </summary>
public static class FileUris
{
    /// <summary>The local path for a file URI, or null when it isn't one (an untitled: or roslyn-source-generated: URI).</summary>
    public static string? ToPath(string? uri) => ToPath(uri, OperatingSystem.IsWindows());

    /// <param name="windows">Whether to read it as a Windows path (a parameter so both kinds can be tested anywhere).</param>
    public static string? ToPath(string? uri, bool windows)
    {
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = uri["file:".Length..];
        string? host = null;
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            var slash = rest.IndexOf('/');
            host = slash < 0 ? rest : rest[..slash];
            rest = slash < 0 ? "/" : rest[slash..];
        }

        // Query and fragment aren't part of the path.
        var end = rest.IndexOfAny(['?', '#']);
        if (end >= 0)
            rest = rest[..end];

        var path = Uri.UnescapeDataString(rest);
        if (!windows)
            return string.IsNullOrEmpty(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? path
                : "//" + host + path;

        // "/c:/src" (or "/c|/src", an old spelling) is the drive c:; anything else with a host is a UNC share.
        if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] is ':' or '|')
            path = char.ToUpperInvariant(path[1]) + ":" + path[3..];
        else if (!string.IsNullOrEmpty(host) && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            path = "//" + host + path;

        path = path.Replace('/', '\\');
        if (path.Length == 2 && path[1] == ':')
            path += "\\";   // "file:///c:" is the drive's root
        return path;
    }

    /// <summary>The file URI for a local path, as this app sends it to servers ("file:///c:/src/a.cs").</summary>
    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>Whether two URIs name the same file, whatever the spelling (drive case, an encoded colon...).</summary>
    public static bool SameFile(string? a, string? b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;
        var (pathA, pathB) = (ToPath(a), ToPath(b));
        return pathA is not null && pathB is not null
               && string.Equals(pathA, pathB, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
