using System;
using System.IO;

namespace AXamlIDE.Services.Diagnostics;

/// <summary>
/// The IDE's own log files, in <c>%AppData%/AXamlIDE/logs</c> (Linux and macOS: the equivalent under ~/.config).
/// Appended to, newest last, and cut to the newer half when one passes a megabyte, so none grows without bound.
/// Writing never throws: a failing log must not take anything else down.
/// </summary>
public static class LogFile
{
    private const long TrimAt = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "AXamlIDE", "logs");

    /// <summary>A log's path in <see cref="Folder"/>, e.g. "crashes.log".</summary>
    public static string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>Append to a log; <paramref name="separator"/> is what ends an entry, so a trim never cuts one in half.
    /// Returns false when it couldn't be written.</summary>
    public static bool Append(string path, string text, string separator = "\n")
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Trim(path, separator);
                File.AppendAllText(path, text);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Trim(string path, string separator)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < TrimAt)
            return;
        var text = File.ReadAllText(path);
        var cut = text.IndexOf(separator, text.Length / 2, StringComparison.Ordinal);
        File.WriteAllText(path, cut < 0 ? "" : text[(cut + separator.Length)..]);
    }
}
