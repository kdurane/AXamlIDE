using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using AXamlIDE.CompletionEngine;
using AXamlIDE.CompletionEngine.AssemblyMetadata;
using AXamlIDE.CompletionEngine.Completion;
using AXamlIDE.CompletionEngine.Parsing;

namespace AXamlIDE.Services.Xaml;

public sealed class XamlCompletionService
{
    private sealed record MetadataEntry(string AssemblyPath, DateTime AssemblyLastWriteUtc, string? AssetsPath, DateTime AssetsLastWriteUtc, Task<MetadataBundle> Metadata);
    private sealed record MetadataBundle(Metadata Metadata, IReadOnlyDictionary<string, string> Documentation, IReadOnlyList<string> AssemblyPaths);
    public sealed record NavigationTarget(string AssemblyPath, string TypeFullName);
    private readonly ConcurrentDictionary<string, MetadataEntry> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly MetadataReader _reader = new(new DnlibMetadataProvider());
    private readonly CompletionEngine.Completion.CompletionEngine _engine = new();
    private readonly object _engineGate = new();

    public async Task<CompletionSet?> GetCompletionsAsync(string projectPath, string assemblyName, string text, int caretOffset, string? outputPath = null, string? assetsPath = null)
    {
        var (targetAssembly, bundle) = await GetMetadataAsync(projectPath, assemblyName, outputPath, assetsPath);
        lock (_engineGate)
            return _engine.GetCompletions(
                bundle.Metadata, text, caretOffset, Path.GetFileNameWithoutExtension(targetAssembly));
    }

    public async Task<string?> GetHoverAsync(string projectPath, string assemblyName, string text, int offset, string? outputPath = null, string? assetsPath = null)
    {
        var (targetAssembly, bundle) = await GetMetadataAsync(projectPath, assemblyName, outputPath, assetsPath);
        offset = Math.Clamp(offset, 0, text.Length);
        var tokenStart = offset;
        var tokenEnd = offset;
        while (tokenStart > 0 && IsXamlNameCharacter(text[tokenStart - 1]))
            tokenStart--;
        while (tokenEnd < text.Length && IsXamlNameCharacter(text[tokenEnd]))
            tokenEnd++;
        if (tokenStart == tokenEnd)
            return null;

        lock (_engineGate)
        {
            _engine.GetCompletions(
                bundle.Metadata, text, tokenEnd, Path.GetFileNameWithoutExtension(targetAssembly));
            var parser = XmlParser.Parse(text.AsMemory(0, tokenEnd));
            var token = text[tokenStart..tokenEnd];

            if (parser.AttributeName is { } attributeName
                && string.Equals(attributeName, token, StringComparison.Ordinal))
            {
                var tagType = _engine.Helper.LookupType(parser.TagName);
                var property = attributeName.Split('.') switch
                {
                    [var declaringType, var propertyName] => _engine.Helper.LookupProperty(declaringType, propertyName),
                    _ => _engine.Helper.LookupProperty(parser.TagName, attributeName)
                };
                if (property is not null)
                {
                    var declaringType = property.DeclaringType?.FullName ?? tagType?.FullName;
                    if (declaringType is not null)
                    {
                        var key = $"P:{declaringType}.{property.Name}";
                        if (!bundle.Documentation.TryGetValue(key, out var summary))
                            bundle.Documentation.TryGetValue($"F:{declaringType}.{property.Name}Property", out summary);
                        var signature = property.Type is null
                            ? $"{declaringType}.{property.Name}"
                            : $"{declaringType}.{property.Name} : {property.Type.FullName}";
                        return FormatHover(signature, summary);
                    }
                }
            }

            if (parser.TagName is not { } tagName
                || tagName.StartsWith("/", StringComparison.Ordinal)
                || (parser.State is not (XmlParser.ParserState.StartElement
                    or XmlParser.ParserState.InsideElement
                    or XmlParser.ParserState.StartAttribute
                    or XmlParser.ParserState.AttributeValue
                    or XmlParser.ParserState.BeforeAttributeValue
                    or XmlParser.ParserState.AfterAttributeValue))) return null;
            {
                var type = _engine.Helper.LookupType(tagName);
                if (type is null) return null;
                bundle.Documentation.TryGetValue($"T:{type.FullName}", out var summary);
                return FormatHover(type.FullName, summary);
            }
        }
    }

    public async Task<NavigationTarget?> GetNavigationTargetAsync(string projectPath, string assemblyName, string text, int caretOffset, string? outputPath = null, string? assetsPath = null)
    {
        var (targetAssembly, bundle) = await GetMetadataAsync(projectPath, assemblyName, outputPath, assetsPath);
        caretOffset = Math.Clamp(caretOffset, 0, text.Length);
        var tokenEnd = caretOffset;
        while (tokenEnd < text.Length && IsXamlNameCharacter(text[tokenEnd]))
            tokenEnd++;

        lock (_engineGate)
        {
            _engine.GetCompletions(
                bundle.Metadata, text, caretOffset, Path.GetFileNameWithoutExtension(targetAssembly));
            var parser = XmlParser.Parse(text.AsMemory(0, tokenEnd));
            if (parser.TagName is not { } tagName)
                return null;

            var type = _engine.Helper.LookupType(tagName);
            if (type?.AssemblyQualifiedName is not { } assemblyQualifiedName)
                return null;

            var definingAssemblyName = GetDefiningAssemblyName(assemblyQualifiedName);
            if (definingAssemblyName is null)
                return null;

            var assemblyPath = bundle.AssemblyPaths.FirstOrDefault(path =>
                string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    definingAssemblyName,
                    StringComparison.OrdinalIgnoreCase));
            return assemblyPath is null ? null : new NavigationTarget(assemblyPath, type.FullName);
        }
    }

    private static string? GetDefiningAssemblyName(string assemblyQualifiedName)
    {
        var genericDepth = 0;
        for (var i = 0; i < assemblyQualifiedName.Length; i++)
        {
            switch (assemblyQualifiedName[i])
            {
                case '[':
                    genericDepth++;
                    break;
                case ']':
                    genericDepth--;
                    break;
                case ',' when genericDepth == 0:
                    return new System.Reflection.AssemblyName(assemblyQualifiedName[(i + 1)..].Trim()).Name;
            }
        }

        return null;
    }

    private async Task<(string AssemblyPath, MetadataBundle Bundle)> GetMetadataAsync(
        string projectPath,
        string assemblyName,
        string? outputPath,
        string? assetsPath)
    {
        var targetAssembly = FindBuiltAssembly(projectPath, assemblyName, outputPath);
        var assemblyLastWriteUtc = File.GetLastWriteTimeUtc(targetAssembly);
        var normalizedAssetsPath = string.IsNullOrWhiteSpace(assetsPath)
            ? null
            : Path.GetFullPath(assetsPath, Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
        var assetsLastWriteUtc = normalizedAssetsPath is not null && File.Exists(normalizedAssetsPath)
            ? File.GetLastWriteTimeUtc(normalizedAssetsPath)
            : DateTime.MinValue;
        var entry = _metadata.AddOrUpdate(
            Path.GetFullPath(projectPath),
            _ => CreateEntry(targetAssembly, assemblyLastWriteUtc, normalizedAssetsPath, assetsLastWriteUtc),
            (_, current) => current.AssemblyPath == targetAssembly
                            && current.AssemblyLastWriteUtc == assemblyLastWriteUtc
                            && current.AssetsPath == normalizedAssetsPath
                            && current.AssetsLastWriteUtc == assetsLastWriteUtc
                ? current
                : CreateEntry(targetAssembly, assemblyLastWriteUtc, normalizedAssetsPath, assetsLastWriteUtc));

        return (targetAssembly, await entry.Metadata);
    }

    private MetadataEntry CreateEntry(string assemblyPath, DateTime assemblyLastWriteUtc, string? assetsPath, DateTime assetsLastWriteUtc)
        => new(
            assemblyPath,
            assemblyLastWriteUtc,
            assetsPath,
            assetsLastWriteUtc,
            Task.Run(() =>
            {
                var provider = new DepsJsonFileAssemblyProvider(assemblyPath, assemblyPath);
                var assemblies = provider.GetAssemblies().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var metadata = _reader.GetForTargetAssembly(new StaticAssemblyProvider(assemblies))
                    ?? throw new InvalidOperationException($"Could not read XAML completion metadata from '{assemblyPath}'.");
                return new MetadataBundle(
                    metadata,
                    ReadDocumentation(assemblies, projectAssetsPath: assetsPath),
                    assemblies);
            }));

    private static string FindBuiltAssembly(string projectPath, string assemblyName, string? outputPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))
            ?? throw new InvalidOperationException($"Project path '{projectPath}' has no parent directory.");
        var outputDirectories = new[]
            {
                      string.IsNullOrWhiteSpace(outputPath) ? null : Path.GetFullPath(outputPath, projectDirectory),
                      Path.Combine(projectDirectory, "bin")
                  }
            .Where(path => path is not null && Directory.Exists(path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Cast<string>();
        var candidates = outputDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, assemblyName + ".dll", SearchOption.AllDirectories))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("ref", StringComparer.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc);

        if (candidates.FirstOrDefault() is { } target)
            return target.FullName;

        throw new FileNotFoundException(
            $"Build '{Path.GetFileName(projectPath)}' first to enable Avalonia XAML completion.");
    }

    private static bool IsXamlNameCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';

    private static string FormatHover(string signature, string? summary)
        => string.IsNullOrWhiteSpace(summary) ? signature : $"{signature}{Environment.NewLine}{summary}";

    private static IReadOnlyDictionary<string, string> ReadDocumentation(IEnumerable<string> assemblies, string? projectAssetsPath)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assemblyPath in assemblies)
        {
            var xmlPath = Path.ChangeExtension(assemblyPath, ".xml");
            if (!File.Exists(xmlPath))
                continue;

            LoadDocumentationFile(xmlPath, summaries);
        }

        if (string.IsNullOrWhiteSpace(projectAssetsPath) || !File.Exists(projectAssetsPath)) return summaries;
        {
            try
            {
                using var assets = JsonDocument.Parse(File.ReadAllText(projectAssetsPath));
                var root = assets.RootElement;
                var packageFolders = root.GetProperty("packageFolders").EnumerateObject()
                    .Select(folder => folder.Name).ToArray();
                var libraries = root.GetProperty("libraries");
                foreach (var target in root.GetProperty("targets").EnumerateObject())
                {
                    foreach (var library in target.Value.EnumerateObject())
                    {
                        if (!library.Value.TryGetProperty("runtime", out var runtime)
                            || !libraries.TryGetProperty(library.Name, out var package)
                            || !package.TryGetProperty("path", out var packagePathElement)
                            || packagePathElement.GetString() is not { } packagePath)
                            continue;

                        foreach (var asset in runtime.EnumerateObject())
                        {
                            if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                continue;

                            foreach (var packageFolder in packageFolders)
                            {
                                var xmlPath = Path.ChangeExtension(
                                    Path.Combine(packageFolder, packagePath, asset.Name), ".xml");
                                if (File.Exists(xmlPath))
                                    LoadDocumentationFile(xmlPath, summaries);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or XmlException or JsonException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AXAML hover] Could not resolve XML documentation from '{projectAssetsPath}': {ex.Message}");
            }
        }

        return summaries;
    }

    private static void LoadDocumentationFile(string xmlPath, IDictionary<string, string> summaries)
    {
        try
        {
            var document = XDocument.Load(xmlPath);
            foreach (var member in document.Descendants("member"))
            {
                var name = (string?)member.Attribute("name");
                var summary = member.Element("summary")?.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(summary)) continue;
                var cleaned = Regex.Replace(summary, @"\s+", " ").Trim();
                summaries.TryAdd(name, cleaned);
            }
        }
        catch (Exception ex) when (ex is IOException or XmlException)
        {
            System.Diagnostics.Debug.WriteLine($"[AXAML hover] Could not read XML documentation '{xmlPath}': {ex.Message}");
        }
    }

    private sealed class StaticAssemblyProvider(IReadOnlyList<string> assemblies) : IAssemblyProvider
    {
        public IEnumerable<string> GetAssemblies() => assemblies;
    }
}
