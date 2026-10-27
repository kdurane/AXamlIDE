using System;
using System.Collections.Generic;

namespace AXamlIDE.Services.Testing;

/// <summary>One line of a stack trace, and where it points when it names a source file (null when it doesn't).</summary>
public sealed record StackFrameLine(string Text, string? Path, int Line);

/// <summary>
/// Splits a .NET stack trace into lines and finds the source location in each, from the
/// "at Ns.Class.Method() in /src/File.cs:line 42" form, so the Test Explorer can link them as Visual Studio does.
/// </summary>
public static class StackTraceFrames
{
    public static IReadOnlyList<StackFrameLine> Parse(string? stackTrace)
    {
        var frames = new List<StackFrameLine>();
        if (string.IsNullOrWhiteSpace(stackTrace))
            return frames;

        foreach (var raw in stackTrace.Split('\n'))
        {
            var text = raw.Trim();
            if (text.Length == 0)
                continue;
            var (path, line) = Location(text);
            frames.Add(new StackFrameLine(text, path, line));
        }

        return frames;
    }

    /// <summary>The " in PATH:line N" at the end of a frame, or (null, 0).</summary>
    private static (string? Path, int Line) Location(string frame)
    {
        const string InMarker = " in ";
        const string LineMarker = ":line ";
        var lineAt = frame.LastIndexOf(LineMarker, StringComparison.Ordinal);
        if (lineAt < 0)
            return (null, 0);
        var inAt = frame.LastIndexOf(InMarker, lineAt, StringComparison.Ordinal);
        if (inAt < 0 || !int.TryParse(frame.AsSpan(lineAt + LineMarker.Length).Trim(), out var line))
            return (null, 0);
        var path = frame[(inAt + InMarker.Length)..lineAt];
        return path.Length == 0 ? (null, 0) : (path, line);
    }
}
