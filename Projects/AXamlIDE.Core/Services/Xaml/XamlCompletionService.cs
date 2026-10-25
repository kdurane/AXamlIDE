using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    private sealed record MetadataBundle(Metadata Metadata, IReadOnlyDictionary<string, string> Documentation, IReadOnlyList<string> AssemblyPaths, IReadOnlyDictionary<string, string> DocumentationFiles);
    public sealed record NavigationTarget(string AssemblyPath, string TypeFullName, string? DocumentationPath);

    /// <summary>
    /// The view-model property a binding path segment names (the caret on "StartupProjects" in
    /// {Binding Run.StartupProjects} gives RunViewModel.StartupProjects).
    /// </summary>
    /// <param name="Owner">Short name of the type the property is on ("RunViewModel").</param>
    /// <param name="OwnerFullName">Its full name, for matching symbols and documentation.</param>
    /// <param name="PropertyType">The property's type, for display ("ObservableCollection&lt;StartupProjectItem&gt;").</param>
    /// <param name="Compiled">Where the type is compiled, for showing decompiled source when it isn't in the workspace.</param>
    public sealed record BindingTarget(string Owner, string OwnerFullName, string Property, string? PropertyType, NavigationTarget? Compiled);
    private sealed record ProjectXamlEntry(int FileCount, DateTime NewestWriteUtc, XamlProjectInfo Info);
    private readonly ConcurrentDictionary<string, MetadataEntry> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProjectXamlEntry> _projectXaml = new(StringComparer.OrdinalIgnoreCase);
    private readonly MetadataReader _reader = new(new DnlibMetadataProvider());
    private readonly CompletionEngine.Completion.CompletionEngine _engine = new();
    private readonly object _engineGate = new();

    public async Task<CompletionSet?> GetCompletionsAsync(string projectPath, string assemblyName, string text, int caretOffset, string? outputPath = null, string? assetsPath = null)
    {
        var (targetAssembly, bundle) = await GetMetadataAsync(projectPath, assemblyName, outputPath, assetsPath);
        var projectXaml = GetProjectXaml(projectPath);
        lock (_engineGate)
            return _engine.GetCompletions(
                bundle.Metadata, text, caretOffset, Path.GetFileNameWithoutExtension(targetAssembly), projectXaml);
    }

    /// <summary>
    /// The x:Keys and selector style classes in the project's .axaml files, for {StaticResource ...} and Classes="...".
    /// Read again when a file is added, removed or saved (the open document's unsaved text is read by the engine itself).
    /// </summary>
    private XamlProjectInfo GetProjectXaml(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (projectDirectory is null || !Directory.Exists(projectDirectory))
            return XamlProjectInfo.Empty;

        var files = Directory.EnumerateFiles(projectDirectory, "*.axaml",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(path => !IsBuildOutput(Path.GetRelativePath(projectDirectory, path)))
            .Select(path => new FileInfo(path))
            .ToList();
        var newest = files.Count == 0 ? DateTime.MinValue : files.Max(file => file.LastWriteTimeUtc);

        if (_projectXaml.TryGetValue(projectDirectory, out var cached)
            && cached.FileCount == files.Count && cached.NewestWriteUtc == newest)
            return cached.Info;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            try
            {
                var xaml = File.ReadAllText(file.FullName);
                keys.UnionWith(XamlTagScanner.ResourceKeys(xaml));
                classes.UnionWith(XamlTagScanner.StyleClasses(xaml));
            }
            catch (IOException)
            {
                // Being written: picked up on the next request
            }
        }

        var info = new XamlProjectInfo(keys, classes);
        _projectXaml[projectDirectory] = new ProjectXamlEntry(files.Count, newest, info);
        return info;

        static bool IsBuildOutput(string relativePath)
        {
            var firstSegment = relativePath.Split(Path.DirectorySeparatorChar)[0];
            return firstSegment is "bin" or "obj" || firstSegment.StartsWith('.');
        }
    }

    public async Task<string?> GetHoverAsync(string projectPath, string assemblyName, string text, int offset, string? outputPath = null, string? assetsPath = null)
    {
        var (targetAssembly, bundle) = await GetMetadataAsync(projectPath, assemblyName, outputPath, assetsPath);
        offset = Math.Clamp(offset, 0, text.Length);

        // On a binding path: the bound property, not the element's.
        if (BindingPathLocator.Find(text, offset) is { } binding)
        {
            if (ResolveBinding(targetAssembly, bundle, text, binding) is not { } target)
                return null;
            bundle.Documentation.TryGetValue($"P:{target.OwnerFullName}.{target.Property}", out var bindingSummary);
            var bindingSignature = target.PropertyType is null
                ? $"{target.Owner}.{target.Property}"
                : $"{target.Owner}.{target.Property} : {target.PropertyType}";
            return FormatHover(bindingSignature, bindingSummary);
        }

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

    /// <summary>The property the binding path at the offset names, or null (not on a binding path, or not resolvable).</summary>
    public async Task<BindingTarget?> GetBindingTargetAsync(string projectPath, string assemblyName, string text, int offset, string? outputPath = null, string? assetsPath = null)
    {
        if (BindingPathLocator.Find(text, Math.Clamp(offset, 0, text.Length)) is not { } binding)
            return null;
        var (targetAssembly, bundle) = await GetMetadataAsync(projectPath, assemblyName, outputPath, assetsPath);
        return ResolveBinding(targetAssembly, bundle, text, binding);
    }

    private BindingTarget? ResolveBinding(string targetAssembly, MetadataBundle bundle, string text, BindingPathAtCaret binding)
    {
        lock (_engineGate)
        {
            _engine.GetCompletions(bundle.Metadata, text, binding.SegmentStart, Path.GetFileNameWithoutExtension(targetAssembly));

            // Parse to the end of the element's start tag, so an x:DataType written after the binding is seen too.
            var parser = XmlParser.Parse(text.AsMemory(0, StartTagEnd(text, binding.SegmentStart)));
            if (_engine.ResolveBindingPathSegment(parser, binding.Path, binding.SegmentIndex) is not var (owner, property))
                return null;

            var ownerFullName = owner.FullName.Length > 0 ? owner.FullName : owner.Name;
            return new BindingTarget(
                DisplayName(owner), ownerFullName, property.Name,
                property.Type is null ? null : DisplayName(property.Type),
                CompiledLocation(bundle, owner));
        }
    }

    /// <summary>
    /// Where the start tag ends (its '&gt;'), from an <paramref name="offset"/> inside one of its attribute values:
    /// past the end of that value, then skipping the other quoted values.
    /// </summary>
    private static int StartTagEnd(string text, int offset)
    {
        var valueEnd = text.IndexOfAny(['"', '\''], offset);
        if (valueEnd < 0)
            return text.Length;

        char? quote = null;
        for (var i = valueEnd + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == '>')
                return text[i - 1] == '/' ? i - 1 : i;
            else if (c == '<')
                return i;
        }
        return text.Length;
    }

    /// <summary>"ObservableCollection&lt;StartupProjectItem&gt;" rather than a metadata name with `1 and namespaces.</summary>
    private static string DisplayName(MetadataType type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick < 0)
            return name;

        // Generic: take the arguments from the full name's [[...]] if they're there.
        var arguments = Regex.Matches(type.FullName, @"\[\[?([^\[\],]+)")
            .Select(match => match.Groups[1].Value.Split('.')[^1].Split('`')[0])
            .ToList();
        return name[..tick] + (arguments.Count > 0 ? $"<{string.Join(", ", arguments)}>" : "<...>");
    }

    private static NavigationTarget? CompiledLocation(MetadataBundle bundle, MetadataType type)
    {
        if (type.AssemblyQualifiedName is not { } qualified || GetDefiningAssemblyName(qualified) is not { } assembly)
            return null;
        var assemblyPath = bundle.AssemblyPaths.FirstOrDefault(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), assembly, StringComparison.OrdinalIgnoreCase));
        return assemblyPath is null ? null : new NavigationTarget(assemblyPath, type.FullName, bundle.DocumentationFiles.GetValueOrDefault(assembly));
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
            return assemblyPath is null ? null : new NavigationTarget(assemblyPath, type.FullName, bundle.DocumentationFiles.GetValueOrDefault(definingAssemblyName));
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
                var (summaries, documentationFiles) = ReadDocumentation(assemblies, projectAssetsPath: assetsPath);
                return new MetadataBundle(metadata, summaries, assemblies, documentationFiles);
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

    private static (IReadOnlyDictionary<string, string> Summaries, IReadOnlyDictionary<string, string> Files)
    ReadDocumentation(IEnumerable<string> assemblies, string? projectAssetsPath)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assemblyPath in assemblies)
        {
            var xmlPath = Path.ChangeExtension(assemblyPath, ".xml");
            if (!File.Exists(xmlPath))
                continue;

            LoadDocumentationFile(xmlPath, summaries);
            files[Path.GetFileNameWithoutExtension(xmlPath)] = xmlPath;
        }


        if (string.IsNullOrWhiteSpace(projectAssetsPath) || !File.Exists(projectAssetsPath)) return (summaries, files);
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
                                {
                                    LoadDocumentationFile(xmlPath, summaries);
                                    files[Path.GetFileNameWithoutExtension(xmlPath)] = xmlPath;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or XmlException or JsonException)
            {
                Debug.WriteLine(
                    $"[AXAML hover] Could not resolve XML documentation from '{projectAssetsPath}': {ex.Message}");
            }
        }

        return (summaries, files);
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
            Debug.WriteLine($"[AXAML hover] Could not read XML documentation '{xmlPath}': {ex.Message}");
        }
    }

    private sealed class StaticAssemblyProvider(IReadOnlyList<string> assemblies) : IAssemblyProvider
    {
        public IEnumerable<string> GetAssemblies() => assemblies;
    }
}
