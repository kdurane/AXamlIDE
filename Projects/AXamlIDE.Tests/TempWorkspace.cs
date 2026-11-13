using System;
using System.IO;

namespace AXamlIDE.Tests;

/// <summary>A throwaway folder for tests that need real files; deleted on Dispose.</summary>
public sealed class TempWorkspace : IDisposable
{
    public string Root { get; } = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "axamlide-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Write a file (creating folders) and return its full path.</summary>
    public string Write(string relativePath, string text)
    {
        var path = PathOf(relativePath);   // the OS's separators, as the code under test returns them
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>The path a test expects for <paramref name="relativePath"/>, written with '/' on every OS.</summary>
    public string PathOf(string relativePath)
        => Path.GetFullPath(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // Git writes its objects read-only, which Windows refuses to delete: clear the flag first.
    public void Dispose()
    {
        if (!Directory.Exists(Root))
            return;
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
