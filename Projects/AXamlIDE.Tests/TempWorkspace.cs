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
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
