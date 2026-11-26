using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;

namespace AXamlIDE.Services.Debugging;

/// <summary>
/// The source of code the debugger decompiled. With Just My Code off, SharpDbg decompiles an assembly that has no PDB
/// when the debugger reaches it (a step into it, a stop in it, a Call Stack frame clicked) and writes a PDB for it to
/// <c>&lt;local app data&gt;/Temp/SharpIdeSymbolCache/&lt;assembly&gt;/&lt;mvid&gt;/&lt;assembly&gt;.decompiled.pdb</c>, with
/// the C# embedded in it. Its frames name a file that doesn't exist, <c>decompiled/&lt;assembly&gt;/&lt;mvid&gt;/&lt;Namespace&gt;/&lt;Type&gt;.cs</c>,
/// so the IDE writes that source out under <see cref="Folder"/>, keeping the same path at the end. Then the editor
/// opens it like any file (read-only: <see cref="IsInside"/>), and a breakpoint set in it reaches the debugger with
/// a path ending in the PDB's own document name, which is how SharpDbg matches breakpoint paths.
/// </summary>
public static class DecompiledSources
{
    private const string Prefix = "decompiled";

    /// <summary>Embedded source, in a Portable PDB's custom debug information.</summary>
    private static readonly Guid EmbeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    /// <summary>Where the sources are written: per user, not inside the app's folder.</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "AXamlIDE", "DecompiledSources");

    /// <summary>Where SharpDbg keeps the PDBs it writes (its own folder name).</summary>
    internal static string SymbolCache { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "SharpIdeSymbolCache");

    /// <summary>A file this wrote: decompiled code, which can't be edited or saved.</summary>
    public static bool IsInside(string path)
    {
        var root = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A path the debugger gave for decompiled code ("decompiled/Asm/mvid/Ns/Type.cs"), not a real file.</summary>
    public static bool IsDecompiledPath(string? path)
        => path is { Length: > 0 } && !Path.IsPathRooted(path) && Parts(path) is [Prefix, _, _, ..];

    /// <summary>
    /// The real file for a decompiled path, written from the PDB if it isn't there yet; null if the PDB or the
    /// source in it can't be found (the path is then left as it was, and the frame shows as external code).
    /// </summary>
    public static string? ToLocalFile(string decompiledPath)
    {
        if (!IsDecompiledPath(decompiledPath))
            return null;
        var parts = Parts(decompiledPath);
        var local = Path.Combine([Folder, .. parts]);
        if (File.Exists(local))
            return local;

        var (assembly, mvid) = (parts[1], parts[2]);
        var pdb = Path.Combine(SymbolCache, assembly, mvid, assembly + ".decompiled.pdb");
        try
        {
            if (!File.Exists(pdb) || ReadEmbeddedSource(pdb, parts) is not { } source)
                return null;
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, source);
            return local;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The C# embedded for the document whose name is these parts, or null.</summary>
    internal static string? ReadEmbeddedSource(string pdbPath, string[] parts)
    {
        using var stream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        foreach (var handle in reader.Documents)
        {
            var name = reader.GetString(reader.GetDocument(handle).Name);
            if (!Parts(name).SequenceEqual(parts, StringComparer.Ordinal))
                continue;
            foreach (var informationHandle in reader.GetCustomDebugInformation(handle))
            {
                var information = reader.GetCustomDebugInformation(informationHandle);
                if (reader.GetGuid(information.Kind) == EmbeddedSource)
                    return Decode(reader.GetBlobBytes(information.Value));
            }
        }
        return null;
    }

    /// <summary>Embedded source's blob: a 4-byte format (0: the text as it is; otherwise its length, deflated), then the text.</summary>
    private static string Decode(byte[] blob)
    {
        var format = BitConverter.ToInt32(blob, 0);
        if (format == 0)
            return Text(blob.AsSpan(4).ToArray());
        using var deflate = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
        var bytes = new byte[format];
        deflate.ReadExactly(bytes);
        return Text(bytes);
    }

    private static string Text(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string[] Parts(string path) => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
