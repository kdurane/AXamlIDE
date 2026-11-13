using System;
using System.IO;
using System.Text;

namespace AXamlIDE.Services.Diagnostics;

/// <summary>
/// Where unhandled exceptions are written: <c>%AppData%/AXamlIDE/logs/crashes.log</c> (Linux and macOS: the
/// equivalent under ~/.config). Appended to, newest last, and trimmed to the last half when it passes a megabyte
/// so it never grows without bound. Writing never throws: a failing log must not take the app down twice.
/// </summary>
public static class CrashLog
{
    private const long TrimAt = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "AXamlIDE", "logs");

    public static string Path_ => Path.Combine(Folder, "crashes.log");

    /// <summary>The file the log goes to.</summary>
    public static string FilePath => Path_;

    /// <summary>
    /// Record an exception. <paramref name="source"/> says where it was caught ("UI thread", "task", "fatal").
    /// Returns the log's path, or null when it couldn't be written.
    /// </summary>
    public static string? Write(string source, Exception exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                Trim();
                var entry = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  [").Append(source).AppendLine("]")
                    .AppendLine(exception.ToString())
                    .AppendLine();
                File.AppendAllText(FilePath, entry.ToString());
            }
            return FilePath;
        }
        catch
        {
            return null;
        }
    }

    private static void Trim()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < TrimAt)
            return;
        var text = File.ReadAllText(FilePath);
        var cut = text.IndexOf("\n\n", text.Length / 2, StringComparison.Ordinal);
        File.WriteAllText(FilePath, cut < 0 ? "" : text[(cut + 2)..]);
    }
}
