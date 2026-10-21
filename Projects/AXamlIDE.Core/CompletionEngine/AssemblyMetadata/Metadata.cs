using System;
using System.Collections.Generic;
using System.Diagnostics;
// ReSharper disable TypeWithSuspiciousEqualityIsUsedInRecord.Global

namespace AXamlIDE.CompletionEngine.AssemblyMetadata;

public class Metadata
{
    private readonly Dictionary<string, Dictionary<string, MetadataType>> _namespaces = [];
    private readonly Dictionary<string, string> _inverseNamespace = [];

    public IReadOnlyDictionary<string, Dictionary<string, MetadataType>> Namespaces => _namespaces;
    public IReadOnlyDictionary<string, string> InverseNamespace => _inverseNamespace;

    public void AddType(string ns, MetadataType type)
    {
        _namespaces.GetOrCreate(ns)[type.Name] = type;
        _inverseNamespace[type.FullName] = ns;
    }

    /// <summary>
    /// Add new metadata. Keys are added and existing keys are unchanged.
    /// </summary>
    public void AddMetadata(Metadata metadata)
    {
        foreach (var x in metadata._namespaces)
            if (!_namespaces.ContainsKey(x.Key))
                _namespaces.Add(x.Key, x.Value);
        foreach (var x in metadata._inverseNamespace)
            if (!_inverseNamespace.ContainsKey(x.Key))
                _inverseNamespace.Add(x.Key, x.Value);
    }
}

// todo: add property for permutation annotation. A MetadataType may be defined in multiple build contexts, but have different definitions.
[DebuggerDisplay("{Name}")]
public record MetadataType(string Name)
{
    public bool IsEnum { get; set; }
    public bool IsMarkupExtension { get; set; }
    public bool IsStatic { get; set; }
    public bool HasHintValues { get; set; }
    public string[]? HintValues { get; set; }

    public string[] PseudoClasses { get; set; } = [];
    public bool HasPseudoClasses { get; set; }

    //assembly, type, property
    public Func<string?, MetadataType, MetadataProperty?, bool>? IsValidForXamlContextFunc { get; set; }
    //assembly, type, property
    public Func<string?, MetadataType, MetadataProperty?, IEnumerable<string>>? XamlContextHintValuesFunc { get; set; }
    public string FullName { get; set; } = "";
    public List<MetadataProperty> Properties { get; set; } = [];
    public List<MetadataEvent> Events { get; set; } = [];
    public bool HasAttachedProperties { get; set; }
    public bool HasAttachedEvents { get; set; }
    public bool HasStaticGetProperties { get; set; }
    public bool HasSetProperties { get; set; }
    public bool IsAvaloniaObjectType { get; set; }
    public MetadataTypeCtorArgument SupportCtorArgument { get; set; }
    public bool IsCompositeValue { get; set; }
    public bool IsGeneric { get; set; }
    public bool IsXamlDirective { get; set; }
    public string? AssemblyQualifiedName { get; set; }
    public bool IsNullable { get; init; }
    public MetadataType? UnderlyingType { get; init; }
    public List<(MetadataType Type, string Name)> TemplateParts { get; set; } = [];
    public bool IsAbstract { get; internal set; } = false;

    /// <summary>Can be written as an element: not abstract, static or an enum, and XAML can construct it.</summary>
    public bool CanBeElement { get; set; }
    /// <summary>Full names of everything this type can be assigned to (itself, base classes, interfaces).</summary>
    public IReadOnlySet<string> AssignableTo { get; set; } = EmptySet;
    /// <summary>What child elements of this collection type must be; empty when it isn't a collection.</summary>
    public string[] ItemTypeNames { get; set; } = [];
    /// <summary>The [Content] property, which the type's child elements are assigned to.</summary>
    public string? ContentPropertyName { get; set; }
    /// <summary>Hint values are the resource keys the project defines ({StaticResource}, {DynamicResource}).</summary>
    public bool HintsResourceKeys { get; set; }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();
}

public enum MetadataTypeCtorArgument
{
    None,
    Type,
    Object,
    TypeAndObject,
    HintValues
}

/// <param name="IsAvaloniaProperty">Backed by an AvaloniaProperty (styled, direct or attached): what Setters, selectors and TemplateBinding can use.</param>
/// <param name="TypeFullName">The property type's full name, also when <paramref name="Type"/> is unknown (an interface, a framework type).</param>
[DebuggerDisplay("{Name} from {DeclaringType}")]
public record MetadataProperty(string Name, MetadataType? Type, MetadataType? DeclaringType, bool IsAttached, bool IsStatic, bool HasGetter, bool HasSetter,
    bool IsAvaloniaProperty = false, string? TypeFullName = null);

public record MetadataEvent(string Name, MetadataType? Type, MetadataType? DeclaringType, bool IsAttached);

