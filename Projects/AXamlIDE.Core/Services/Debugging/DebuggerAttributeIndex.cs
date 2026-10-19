using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AXamlIDE.Services.Debugging;

/// <summary>
/// The debugger attributes netcoredbg ignores, read from the debuggee's assemblies (their metadata, not by loading
/// them): [DebuggerDisplay("{City}, {Street}")] on types and [DebuggerBrowsable(Never)] on members. Assemblies are
/// read once each, the first time a type is looked up after they've loaded.
/// </summary>
public sealed class DebuggerAttributeIndex
{
    private readonly Func<IReadOnlyCollection<string>> _modules;
    private readonly ConcurrentDictionary<string, byte> _read = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _displays = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _hidden = new(StringComparer.Ordinal);

    /// <param name="modules">The assemblies loaded so far (NetCoreDbgSession.LoadedModules).</param>
    public DebuggerAttributeIndex(Func<IReadOnlyCollection<string>> modules) => _modules = modules;

    /// <summary>The [DebuggerDisplay] format for a type as the adapter names it ("MyApp.Box&lt;int&gt;"), if it has one.</summary>
    public string? DisplayFormatFor(string? typeName)
    {
        if (typeName is null)
            return null;
        ReadNewModules();
        return _displays.GetValueOrDefault(Key(typeName));
    }

    /// <summary>Members marked [DebuggerBrowsable(DebuggerBrowsableState.Never)] on a type.</summary>
    public bool IsHidden(string? typeName, string member)
    {
        if (typeName is null)
            return false;
        ReadNewModules();
        return _hidden.TryGetValue(Key(typeName), out var members) && members.Contains(member);
    }

    /// <summary>"System.Collections.Generic.Dictionary&lt;string, int&gt;.Entry" -> "System.Collections.Generic.Dictionary.Entry".</summary>
    public static string Key(string typeName)
    {
        var key = new System.Text.StringBuilder(typeName.Length);
        var depth = 0;
        foreach (var c in typeName)
        {
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (depth == 0) key.Append(c);
        }
        return key.ToString().Trim();
    }

    private int _modulesSeen;

    private void ReadNewModules()
    {
        var modules = _modules();
        if (modules.Count == _modulesSeen)
            return;   // nothing new loaded (this runs for every member of every row in a table)
        _modulesSeen = modules.Count;
        foreach (var module in modules)
            if (_read.TryAdd(module, 0))
                ReadModule(module);
    }

    private void ReadModule(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return;
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                string? key = null;
                string TypeKey() => key ??= FullName(metadata, type);

                foreach (var attribute in type.GetCustomAttributes())
                    if (AttributeName(metadata, attribute) == "DebuggerDisplayAttribute" && FirstStringArgument(metadata, attribute) is { } format)
                        _displays[TypeKey()] = format;

                foreach (var field in type.GetFields())
                {
                    var definition = metadata.GetFieldDefinition(field);
                    if (IsNeverBrowsable(metadata, definition.GetCustomAttributes()))
                        _hidden.GetOrAdd(TypeKey(), _ => []).Add(metadata.GetString(definition.Name));
                }
                foreach (var property in type.GetProperties())
                {
                    var definition = metadata.GetPropertyDefinition(property);
                    if (IsNeverBrowsable(metadata, definition.GetCustomAttributes()))
                        _hidden.GetOrAdd(TypeKey(), _ => []).Add(metadata.GetString(definition.Name));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            // Not a .NET assembly we can read: no attributes from it.
        }
    }

    private static bool IsNeverBrowsable(MetadataReader metadata, CustomAttributeHandleCollection attributes)
    {
        foreach (var attribute in attributes)
        {
            if (AttributeName(metadata, attribute) != "DebuggerBrowsableAttribute")
                continue;
            var blob = metadata.GetBlobReader(metadata.GetCustomAttribute(attribute).Value);
            if (blob.Length >= 6 && blob.ReadUInt16() == 1)
                return blob.ReadInt32() == 0;   // DebuggerBrowsableState.Never
        }
        return false;
    }

    /// <summary>The attribute's type name (without namespace).</summary>
    private static string? AttributeName(MetadataReader metadata, CustomAttributeHandle handle)
    {
        var constructor = metadata.GetCustomAttribute(handle).Constructor;
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                var parent = metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                return parent.Kind == HandleKind.TypeReference
                    ? metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name)
                    : null;
            case HandleKind.MethodDefinition:
                var declaring = metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
                return metadata.GetString(metadata.GetTypeDefinition(declaring).Name);
            default:
                return null;
        }
    }

    /// <summary>The first constructor argument when it's a string (custom attribute blob: prolog 0x0001, SerString).</summary>
    private static string? FirstStringArgument(MetadataReader metadata, CustomAttributeHandle handle)
    {
        var blob = metadata.GetBlobReader(metadata.GetCustomAttribute(handle).Value);
        if (blob.Length < 3 || blob.ReadUInt16() != 1)
            return null;
        return blob.ReadSerializedString();
    }

    /// <summary>Namespace.Outer.Inner, generic arity (`1) removed: the same shape as <see cref="Key"/>.</summary>
    private static string FullName(MetadataReader metadata, TypeDefinition type)
    {
        var names = new List<string>();
        var current = type;
        while (true)
        {
            var name = metadata.GetString(current.Name);
            var tick = name.IndexOf('`');
            names.Add(tick < 0 ? name : name[..tick]);
            var declaring = current.GetDeclaringType();
            if (declaring.IsNil)
                break;
            current = metadata.GetTypeDefinition(declaring);
        }
        names.Reverse();
        var ns = metadata.GetString(current.Namespace);
        var joined = string.Join(".", names);
        return ns.Length == 0 ? joined : ns + "." + joined;
    }
}
