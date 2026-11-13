using System;
using System.Collections.Generic;
using System.Linq;

namespace AXamlIDE.CompletionEngine;

/// <summary>
/// Everything the completion engine knows about Avalonia by name rather than by reading the assemblies. Almost all
/// of the metadata (controls, properties, events, enums, markup extensions, xmlns) is read from the DLLs and needs
/// nothing here; what's left falls into two groups:
///
///   - <see cref="KnownTypes"/>: Avalonia types that get special completions (binding paths, resource keys, brush
///     names, image files...). If Avalonia renames or moves one, that completion stops working silently, so
///     XamlIntrinsicsTests checks every one still resolves against the Avalonia version the app is built with.
///   - The XAML compiler's own knowledge, which isn't in any DLL: the x: directives and markup extensions, the
///     OnPlatform / OnFormFactor elements, and get-only collections the compiler fills from a string.
///
/// When Avalonia adds compiler features (a new x: directive, a new intrinsic), this is the one place to add them.
/// </summary>
public static class XamlIntrinsics
{
    /// <summary>
    /// Avalonia types the engine adds completions to, by full name. <c>true</c> = required: a test fails if it's missing.
    /// <c>false</c> = optional: an alternative is used when it's absent (Avalonia 12 has no BindingExtension type;
    /// {Binding} creates an Avalonia.Data.Binding, which is used instead).
    /// </summary>
    public static IReadOnlyList<(string FullName, bool Required)> KnownTypes { get; } =
    [
        ("Avalonia.Markup.Xaml.MarkupExtensions.BindingExtension", false),
        ("Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindingExtension", false),
        ("Avalonia.Data.Binding", false),
        ("Avalonia.Controls.Control", true),
        ("Avalonia.Data.TemplateBinding", true),
        ("Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension", true),
        ("Avalonia.Markup.Xaml.MarkupExtensions.StaticResourceExtension", true),
        ("Avalonia.Media.Brushes", true),
        ("Avalonia.Styling.Selector", true),
        ("Avalonia.Media.Imaging.IBitmap", false),   // also made by hand (MetadataConverter.PreProcessTypes)
        ("Avalonia.Media.IImage", true),
        ("Avalonia.Controls.WindowIcon", true),
        ("Avalonia.Markup.Xaml.Styling.StyleIncludeExtension", false),
        ("Avalonia.Markup.Xaml.Styling.StyleInclude", true),
    ];

    /// <summary>
    /// The prefixes MetadataConverter matches type keys against: a trailing ',' means "exactly this type" (the key
    /// is assembly-qualified, "Name, Assembly"); without one, generic and nested names starting with it match too.
    /// </summary>
    internal static readonly string[] KnownTypeKeyPrefixes =
    [
        .. KnownTypes.Select(t => t.FullName is "Avalonia.Media.Brushes" or "Avalonia.Media.Imaging.IBitmap" or "Avalonia.Media.IImage"
            ? t.FullName
            : t.FullName + ",")
    ];

    /// <summary>
    /// Bitmap types take an image file in XAML (the loader's bitmap converter): anything assignable to one of these
    /// gets image-file completions, so a new bitmap type needs no change. (IImage itself does too; a DrawingImage,
    /// also an IImage, doesn't, which is why this isn't IImage.)
    /// </summary>
    public static IReadOnlyList<string> ImageTypes { get; } = ["Avalonia.Media.Imaging.IBitmap", "Avalonia.Media.Imaging.Bitmap"];

    /// <summary>
    /// Get-only collections the XAML compiler fills from an attribute value (Classes="a b" adds each class;
    /// &lt;On Options="Windows, macOS"&gt; adds each platform), so they're offered as attributes.
    /// </summary>
    public static IReadOnlyList<(string DeclaringType, string Property)> SettableFromString { get; } =
    [
        ("Avalonia.StyledElement", "Classes"),
        ("Avalonia.Markup.Xaml.MarkupExtensions.On`1", "Options"),
    ];

    public static bool IsSettableFromString(string declaringType, string property)
        => SettableFromString.Contains((declaringType, property));

    /// <summary>The compiler's OnPlatform / OnFormFactor: their attributes are platform or form-factor names.</summary>
    public const string OnPlatform = "OnPlatform";
    public const string OnFormFactor = "OnFormFactor";

    /// <summary>&lt;On Options="..."&gt; under OnPlatform: the platform names (OnPlatformExtension's properties).</summary>
    public static IReadOnlyList<string> Platforms { get; } = ["Windows", "macOS", "Linux", "Android", "iOS", "Browser"];

    /// <summary>&lt;On Options="..."&gt; under OnFormFactor (OnFormFactorExtension's properties).</summary>
    public static IReadOnlyList<string> FormFactors { get; } = ["Desktop", "Mobile", "TV"];

    /// <summary>Their markup-extension types, whose ProvideValue the metadata reader can't see.</summary>
    public static IReadOnlyList<string> CompilerMarkupExtensions { get; } = ["OnPlatformExtension", "OnFormFactorExtension"];

    /// <summary>
    /// The x: namespace's directives and markup extensions (x:Class, x:Name, {x:Static}...). They live in the XAML
    /// compiler, so MetadataConverter.PreProcessTypes makes them by hand; a new one is added there and here.
    /// </summary>
    public static IReadOnlyList<string> XNamespaceTypes { get; } =
        ["StaticExtension", "TypeExtension", "NullExtension", "Class", "Name", "Key", "DataType", "CompileBindings", "True", "False"];

    public static bool IsOnPlatformOrFormFactor(string? name)
        => string.Equals(name?.Trim(), OnPlatform, StringComparison.Ordinal)
           || string.Equals(name?.Trim(), OnFormFactor, StringComparison.Ordinal);
}
