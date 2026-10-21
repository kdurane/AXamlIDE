using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AXamlIDE.Services.Workspace;

/// <summary>
/// The explorer's file operations on disk: rename, paste (copy or move) and delete (to the trash).
/// Failures throw <see cref="IOException"/> with a message for the status bar.
/// </summary>
public static class FileOperations
{
    /// <summary>Why <paramref name="name"/> can't be a file or folder name, or null if it can.</summary>
    public static string? InvalidNameReason(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "The name can't be empty.";
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            return $"'{name}' isn't a valid name.";
        return null;
    }

    /// <summary>Rename a file or folder in place; returns its new path.</summary>
    public static string Rename(string path, string newName)
    {
        newName = newName.Trim();
        if (InvalidNameReason(newName) is { } reason)
            throw new IOException(reason);

        var target = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!, newName);
        if (target == path)
            return path;
        if (Exists(target))
            throw new IOException($"'{newName}' already exists here.");

        if (Directory.Exists(path))
            Directory.Move(path, target);
        else
            File.Move(path, target);
        return target;
    }

    /// <summary>
    /// Copy (or with <paramref name="move"/>, move) a file or folder into <paramref name="targetFolder"/>; returns
    /// the new path. A copy into a folder that already has that name gets "name - Copy.ext" (as Visual Studio does).
    /// </summary>
    public static string Paste(string source, string targetFolder, bool move)
    {
        source = Path.TrimEndingDirectorySeparator(source);
        if (!Exists(source))
            throw new IOException($"'{Path.GetFileName(source)}' no longer exists.");
        var isFolder = Directory.Exists(source);
        if (isFolder && IsSameOrInside(targetFolder, source))
            throw new IOException($"Can't put '{Path.GetFileName(source)}' inside itself.");

        var sameFolder = Path.GetDirectoryName(source) == Path.TrimEndingDirectorySeparator(targetFolder);
        if (move && sameFolder)
            return source;   // cut and pasted back where it was

        var target = Path.Combine(targetFolder, Path.GetFileName(source));
        var codeBehind = CodeBehindOf(source);
        if (Exists(target) || (codeBehind is not null && Exists(target + ".cs")))
        {
            if (move)
                throw new IOException($"'{Path.GetFileName(source)}' already exists in '{Path.GetFileName(targetFolder)}'.");
            target = UniqueCopyPath(targetFolder, Path.GetFileName(source), isFolder);
        }

        if (move)
        {
            if (isFolder)
                Directory.Move(source, target);
            else
                File.Move(source, target);
        }
        else if (isFolder)
            CopyFolder(source, target);
        else
            File.Copy(source, target);


        if (codeBehind is not null)
        {
            if (move)
                File.Move(codeBehind, target + ".cs");
            else
                File.Copy(codeBehind, target + ".cs");
        }

        return target;
    }

    /// <summary>"name - Copy.ext", then "name - Copy (2).ext"... whichever is free.</summary>
    public static string UniqueCopyPath(string folder, string name, bool isFolder)
    {
        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isFolder ? "" : Path.GetExtension(name);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} - Copy{(n == 1 ? "" : $" ({n})")}{extension}");
            if (!Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Move to the trash / Recycle Bin, so it can be restored. False when that isn't possible here
    /// (no trash available); then the caller can offer to delete permanently.
    /// </summary>
    public static bool TryMoveToTrash(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }

        try
        {
            // Linux desktops (GNOME, KDE...): gio follows the freedesktop trash spec.
            using var process = Process.Start(new ProcessStartInfo("gio") { ArgumentList = { "trash", "--", path }, UseShellExecute = false, RedirectStandardError = true });
            if (process is null)
                return false;
            process.WaitForExit();
            return process.ExitCode == 0 && !Exists(path);
        }
        catch (Win32Exception)
        {
            return false;   // no gio
        }
    }

    public static void DeletePermanently(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return path.Equals(folder, comparison) || path.StartsWith(folder + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// An .axaml file's code-behind (MyView.axaml.cs), which goes wherever the .axaml goes, as in Visual Studio.
    /// Null when the path isn't an .axaml file or has no code-behind.
    /// </summary>
    public static string? CodeBehindOf(string path)
        => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) && File.Exists(path + ".cs")
            ? path + ".cs"
            : null;


    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var folder in Directory.GetDirectories(source))
            CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
    }
}
