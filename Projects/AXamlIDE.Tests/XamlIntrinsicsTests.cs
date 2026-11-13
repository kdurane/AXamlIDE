using System;
using System.Collections.Generic;
using System.Linq;
using AXamlIDE.CompletionEngine;
using AXamlIDE.CompletionEngine.AssemblyMetadata;

namespace AXamlIDE.Tests;

/// <summary>
/// Canaries for the completion engine's hard-coded Avalonia knowledge (XamlIntrinsics), against the Avalonia
/// assemblies this build uses. After an Avalonia upgrade, a failure here names what moved or was added, instead of
/// a completion quietly disappearing.
/// </summary>
public class XamlIntrinsicsTests(AvaloniaMetadataFixture fixture) : IClassFixture<AvaloniaMetadataFixture>
{
    private static readonly Lazy<Dictionary<string, ITypeInformation>> AvaloniaTypes = new(() =>
    {
        var testAssembly = typeof(XamlIntrinsicsTests).Assembly.Location;
        var paths = new DepsJsonFileAssemblyProvider(testAssembly, testAssembly).GetAssemblies().Distinct()
            .Where(path => System.IO.Path.GetFileName(path).StartsWith("Avalonia", StringComparison.Ordinal));
        var session = new DnlibMetadataProvider().GetMetadata(paths);
        var types = new Dictionary<string, ITypeInformation>(StringComparer.Ordinal);
        foreach (var assembly in session.Assemblies)
            foreach (var type in assembly.Types)
                types.TryAdd(type.FullName, type);
        return types;
    });

    private static ITypeInformation? Find(string fullName) => AvaloniaTypes.Value.GetValueOrDefault(fullName);

    [Fact]
    public void Every_required_known_type_still_exists_in_Avalonia()
    {
        var missing = XamlIntrinsics.KnownTypes.Where(t => t.Required && Find(t.FullName) is null).Select(t => t.FullName).ToList();
        Assert.True(missing.Count == 0,
            $"Avalonia no longer has these types (renamed or moved?); update XamlIntrinsics.KnownTypes: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Binding_has_one_of_its_two_shapes()
        => Assert.True(Find("Avalonia.Markup.Xaml.MarkupExtensions.BindingExtension") is not null || Find("Avalonia.Data.Binding") is not null,
            "Neither BindingExtension nor Avalonia.Data.Binding exists: {Binding} completion needs a new type name.");

    [Fact]
    public void Settable_from_string_properties_still_exist()
    {
        foreach (var (declaringType, property) in XamlIntrinsics.SettableFromString)
        {
            var type = Find(declaringType);
            Assert.True(type is not null, $"{declaringType} is gone (XamlIntrinsics.SettableFromString)");
            Assert.True(type.Properties.Any(p => p.Name == property), $"{declaringType}.{property} is gone (XamlIntrinsics.SettableFromString)");
        }
    }

    [Fact]
    public void The_platform_and_form_factor_lists_match_their_markup_extensions()
    {
        AssertNamesMatch("OnPlatformExtension", XamlIntrinsics.Platforms);
        AssertNamesMatch("OnFormFactorExtension", XamlIntrinsics.FormFactors);
    }

    /// <summary>
    /// The extension's own settable properties, apart from the ones every On* extension has, are the names: one
    /// Avalonia adds (a new platform) shows up here as missing from the list.
    /// </summary>
    private static void AssertNamesMatch(string extensionName, IReadOnlyList<string> names)
    {
        var type = AvaloniaTypes.Value.Values.FirstOrDefault(t => t.Name == extensionName);
        Assert.True(type is not null, $"{extensionName} is gone (XamlIntrinsics.CompilerMarkupExtensions)");
        string[] common = ["Default", "Key", "Content"];
        // The names may be declared on a shared base class (OnPlatformExtensionBase...): walk up to it.
        var actual = new HashSet<string>();
        for (var current = type; current is not null && current.FullName != "System.Object"; current = current.GetBaseType())
            foreach (var p in current.Properties.Where(p => p.HasPublicSetter && !p.IsStatic && !common.Contains(p.Name)))
                actual.Add(p.Name);
        var unknown = actual.Except(names).ToList();
        var gone = names.Except(actual).ToList();
        Assert.True(unknown.Count == 0 && gone.Count == 0,
            $"{extensionName}'s names changed. New: [{string.Join(", ", unknown)}]. Gone: [{string.Join(", ", gone)}]. Update XamlIntrinsics.");
    }

    [Fact]
    public void Every_x_namespace_directive_is_offered()
    {
        Assert.True(fixture.Metadata.Namespaces.TryGetValue(Utils.Xaml2006Namespace, out var xTypes));
        var missing = XamlIntrinsics.XNamespaceTypes.Where(name => !xTypes.ContainsKey(name)).ToList();
        Assert.True(missing.Count == 0, $"x: types not registered: {string.Join(", ", missing)} (MetadataConverter.PreProcessTypes)");
    }

    [Fact]
    public void Avalonia_properties_are_recognised_by_their_base_type()
    {
        // Layoutable.WidthProperty is a StyledProperty<double>: found by walking its base types to AvaloniaProperty.
        var layoutable = Find("Avalonia.Layout.Layoutable")!;
        Assert.True(layoutable.Fields.Single(f => f.Name == "WidthProperty").IsAvaloniaProperty);
        // A routed event is not an Avalonia property.
        var input = Find("Avalonia.Input.InputElement")!;
        Assert.False(input.Fields.Single(f => f.Name == "PointerPressedEvent").IsAvaloniaProperty);
    }

    [Fact]
    public void Bitmap_types_offer_image_files_without_being_listed_by_name()
    {
        var bitmap = fixture.Metadata.Namespaces.Values.SelectMany(types => types.Values)
            .FirstOrDefault(type => type.FullName == "Avalonia.Media.Imaging.Bitmap");
        Assert.NotNull(bitmap);
        Assert.True(bitmap.HasHintValues);
    }
}
