using System;
using System.Text;

namespace AXamlIDE.Services.Diagnostics;

/// <summary>
/// Where unhandled exceptions are written: <c>crashes.log</c> in <see cref="LogFile.Folder"/>, newest last.
/// Writing never throws: a failing log must not take the app down twice.
/// </summary>
public static class CrashLog
{
    public static string Folder => LogFile.Folder;

    /// <summary>The file the log goes to.</summary>
    public static string FilePath { get; } = LogFile.PathOf("crashes.log");

    /// <summary>
    /// Record an exception. <paramref name="source"/> says where it was caught ("UI thread", "task", "fatal").
    /// Returns the log's path, or null when it couldn't be written.
    /// </summary>
    public static string? Write(string source, Exception exception)
    {
        var entry = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  [").Append(source).AppendLine("]")
            .AppendLine(exception.ToString())
            .AppendLine();
        return LogFile.Append(FilePath, entry.ToString(), separator: "\n\n") ? FilePath : null;
    }
}
