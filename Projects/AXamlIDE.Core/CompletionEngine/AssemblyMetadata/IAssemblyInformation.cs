using System.Collections.Generic;
using System.IO;

namespace AXamlIDE.CompletionEngine.AssemblyMetadata;

public interface IAssemblyInformation
{
    string Name { get; }
    IEnumerable<ITypeInformation> Types { get; }
    IEnumerable<ICustomAttributeInformation> CustomAttributes { get; }
    IEnumerable<string> ManifestResourceNames { get; }
    Stream GetManifestResourceStream(string name);
    IEnumerable<string> InternalsVisibleTo { get; }
    string AssemblyName { get; }
    string PublicKey { get; }
}

public interface ICustomAttributeInformation
{
    string TypeFullName { get; }
    IList<IAttributeConstructorArgumentInformation> ConstructorArguments { get; }
}

public interface IAttributeConstructorArgumentInformation
{
    object? Value { get; }
}

public interface ITypeInformation
{
    string FullName { get; }
    string Name { get; }
    string Namespace { get; }
    string AssemblyQualifiedName { get; }

    ITypeInformation? GetBaseType();
    IEnumerable<IMethodInformation> Methods { get; }
    IEnumerable<IPropertyInformation> Properties { get; }
    IEnumerable<IEventInformation> Events { get; }
    IEnumerable<IFieldInformation> Fields { get; }
    IEnumerable<string> Pseudoclasses { get; }
    IEnumerable<(ITypeInformation Type, string Name)> TemplateParts { get; }

    bool IsEnum { get; }
    bool IsStatic { get; }
    bool IsInterface { get; }
    bool IsPublic { get; }
    bool IsGeneric { get; }
    bool IsAbstract { get; }
    bool IsInternal { get; }
    bool IsValueType { get; }
    /// <summary>Has a parameterless constructor XAML can call (public, or internal in the project's own assembly).</summary>
    bool HasDefaultConstructor { get; }
    IEnumerable<string> EnumValues { get; }
    IEnumerable<ITypeInformation> NestedTypes { get; }

    /// <summary>
    /// Full names of everything this type can be assigned to: itself, its base classes and all their interfaces,
    /// with generic arguments filled in ("System.Collections.Generic.ICollection`1&lt;Avalonia.Controls.Control&gt;").
    /// </summary>
    IEnumerable<string> AssignableTypeNames { get; }

    /// <summary>
    /// When this type is a collection XAML adds child elements to (ICollection&lt;T&gt;, IAddChild&lt;T&gt;): the full
    /// names of what those children must be. "System.Object" when anything goes; empty when it isn't a collection.
    /// </summary>
    IEnumerable<string> CollectionItemTypeNames { get; }
}

public interface IMethodInformation
{
    bool IsStatic { get; }
    bool IsPublic { get; }
    string Name { get; }
    IList<IParameterInformation> Parameters { get; }
    string ReturnTypeFullName { get; }
    string QualifiedReturnTypeFullName { get; }
}

public interface IFieldInformation
{
    bool IsStatic { get; }
    bool IsPublic { get; }
    string Name { get; }
    string ReturnTypeFullName { get; }
    string QualifiedTypeFullName { get; }
    bool IsRoutedEvent { get; }
}

public interface IParameterInformation
{
    string TypeFullName { get; }
    string QualifiedTypeFullName { get; }
}

public interface IPropertyInformation
{
    bool IsStatic { get; }
    bool IsPublic { get; }
    bool IsInternal { get; }
    bool HasPublicSetter { get; }
    bool HasPublicGetter { get; }
    string TypeFullName { get; }
    string QualifiedTypeFullName { get; }
    string Name { get; }
    /// <summary>Marked [Content]: child elements of the declaring type are assigned to this property.</summary>
    bool IsContent { get; }
    bool IsVisbleTo(IAssemblyInformation assembly);
}

public interface IEventInformation
{
    string Name { get; }
    string TypeFullName { get; }
    string QualifiedTypeFullName { get; }
    bool IsPublic { get; }
    bool IsInternal { get; }
}
