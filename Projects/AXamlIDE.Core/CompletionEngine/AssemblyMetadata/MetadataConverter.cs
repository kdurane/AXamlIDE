using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AXamlIDE.CompletionEngine.AssemblyMetadata;

internal record AvaloniaResourcesIndexEntry(string? Path, int Offset, int Size);

public static class MetadataConverter
{
    private const int LegacyXmlResourceIndex = 1;
    private const int CurrentResourceIndex = 2;

    private static readonly string[] SAvaloniaBaseType =
    [
        "Avalonia.Markup.Xaml.MarkupExtensions.BindingExtension,",
                  "Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindingExtension,",
                  "Avalonia.Data.Binding,",
                  "Avalonia.Controls.Control,",
                  "Avalonia.Data.TemplateBinding,",
                  "Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension,",
                  "Avalonia.Markup.Xaml.MarkupExtensions.StaticResourceExtension,",
                  "Avalonia.Media.Brushes",
                  "Avalonia.Styling.Selector,",
                  "Avalonia.Media.Imaging.IBitmap",
                  "Avalonia.Media.IImage",
                  "Avalonia.Controls.WindowIcon,",
                  "Avalonia.Markup.Xaml.Styling.StyleIncludeExtension,",
                  "Avalonia.Markup.Xaml.Styling.StyleInclude,",
              ];

    private const string NullablePrefix = "System.Nullable`1";

    /// <summary>"X" from "System.Nullable`1&lt;X&gt;" (dnlib's form) or "System.Nullable`1[[X, Assembly]]" (reflection's).</summary>
    private static string? NullableArgument(string typeName)
    {
        if (!typeName.StartsWith(NullablePrefix, StringComparison.Ordinal))
            return null;
        var rest = typeName.AsSpan(NullablePrefix.Length);
        if (rest.StartsWith("<"))
        {
            var end = rest.LastIndexOf('>');
            return end > 1 ? rest[1..end].ToString() : null;
        }
        if (rest.StartsWith("[["))
        {
            var end = rest.IndexOfAny(',', ']');
            return end > 2 ? rest[2..end].ToString() : null;
        }
        return null;
    }

    /// <summary>
    /// Get-only collections the XAML compiler fills from an attribute value: Classes="a b" adds each class,
    /// &lt;On Options="Windows, macOS"&gt; adds each platform. Treated as settable so they're offered as attributes.
    /// </summary>
    private static bool IsSettableFromXaml(string declaringType, string property)
        => (declaringType, property) is ("Avalonia.StyledElement", "Classes") or ("Avalonia.Markup.Xaml.MarkupExtensions.On`1", "Options");

    private static bool IsAvaloniaPropertyField(IFieldInformation field)
        => field.IsStatic
           && field.Name.EndsWith("Property", StringComparison.Ordinal)
           && (field.ReturnTypeFullName.StartsWith("Avalonia.StyledProperty`1", StringComparison.Ordinal)
               || field.ReturnTypeFullName.StartsWith("Avalonia.DirectProperty`2", StringComparison.Ordinal)
               || field.ReturnTypeFullName.StartsWith("Avalonia.AttachedProperty`1", StringComparison.Ordinal)
               || field.ReturnTypeFullName == "Avalonia.AvaloniaProperty");

    private static bool IsMarkupExtension(ITypeInformation type)
    {
        var def = type;

        while (def != null)
        {
            if (def.Name == "MarkupExtension")
                return true;
            def = def.GetBaseType();
        }

        // XAML also treats any *Extension type with a ProvideValue method as a markup extension
        if (type.FullName.EndsWith("Extension") && type.Methods.Any(m => m.Name == "ProvideValue"))
        {
            return true;
        }
        else if (type.Name.Equals("OnPlatformExtension") || type.Name.Equals("OnFormFactorExtension"))
        {
            // Special case for this, as it the type info can't find the ProvideValue method
            return true;
        }

        return false;
    }

    private static MetadataType ConvertTypeInformation(ITypeInformation type)
    {
        var isMarkupExtension = IsMarkupExtension(type);
        var assignableTo = new HashSet<string>(type.AssignableTypeNames);
        var mt = new MetadataType(type.Name)
        {
            FullName = type.FullName,
            AssemblyQualifiedName = type.AssemblyQualifiedName,
            IsStatic = type.IsStatic,
            IsMarkupExtension = isMarkupExtension,
            IsEnum = type.IsEnum,
            HasHintValues = type.IsEnum,
            IsGeneric = type.IsGeneric,
            IsAbstract = type.IsAbstract,
            CanBeElement = !type.IsAbstract && !type.IsStatic && !type.IsEnum
                           && (type.HasDefaultConstructor || type.IsValueType || isMarkupExtension)
                           && !assignableTo.Contains("System.Attribute")
                           && !assignableTo.Contains("System.Exception")
                           && !assignableTo.Contains("System.EventArgs"),
            AssignableTo = assignableTo,
            // Styles and ControlThemes take any child (IAddChild), but only setters and nested styles make sense.
            ItemTypeNames = assignableTo.Contains("Avalonia.Styling.StyleBase")
                ? ["Avalonia.Styling.SetterBase", "Avalonia.Styling.IStyle"]
                : [.. type.CollectionItemTypeNames],
        };
        if (mt.IsEnum)
            mt.HintValues = [.. type.EnumValues];
        return mt;
    }

    private record AvaresInfo(IAssemblyInformation Assembly, string ReturnTypeFullName, string LocalUrl, string GlobalUrl)
    {
        public override string ToString() => GlobalUrl;
    }

    public static Metadata ConvertMetadata(IMetadataReaderSession provider)
    {
        var types = new Dictionary<string, MetadataType>();
        // By reference: MetadataType is a record whose value (and hash) changes as it's filled in.
        var typeDefs = new Dictionary<MetadataType, ITypeInformation>(ReferenceEqualityComparer.Instance);
        var metadata = new Metadata();
        var resourceUrls = new List<string>();
        var avaresValues = new List<AvaresInfo>();
        var pseudoclasses = new HashSet<string>();
        var typepseudoclasses = new HashSet<string>();

        var ignoredResExt = new[] { ".resources", ".rd.xml", "!AvaloniaResources" };

        PreProcessTypes(types, metadata);
        var targetAssembly = provider.Assemblies.FirstOrDefault() ?? throw new InvalidOperationException("IMetadataReaderSession.Assemblies list is empty.");
        foreach (var asm in provider.Assemblies)
        {
            var aliases = new Dictionary<string, string[]>();

            ProcessCustomAttributes(asm, aliases);

            Func<ITypeInformation, bool> typeFilter = type => type is { IsInterface: false, IsPublic: true };

            if (asm.AssemblyName == provider.TargetAssemblyName || asm.InternalsVisibleTo.Any(att =>
                {
                    var endNameIndex = att.IndexOf(',');
                    var assemblyName = att;
                    var targetPublicKey = targetAssembly.PublicKey;
                    if (endNameIndex > 0)
                    {
                        assemblyName = att[..endNameIndex];
                    }

                    if (assemblyName != targetAssembly.Name) return false;
                    if (endNameIndex == -1)
                    {
                        return true;
                    }
                    var publicKeyIndex = att.IndexOf("PublicKey", endNameIndex, StringComparison.OrdinalIgnoreCase);
                    if (publicKeyIndex <= 0) return false;
                    publicKeyIndex += 9;
                    if (publicKeyIndex > att.Length)
                    {
                        return false;
                    }
                    while (publicKeyIndex < att.Length && att[publicKeyIndex] is ' ' or '=')
                    {
                        publicKeyIndex++;
                    }

                    if (targetPublicKey.Length != att.Length - publicKeyIndex) return false;
                    for (var i = publicKeyIndex; i < att.Length; i++)
                    {
                        if (att[i] != targetPublicKey[i - publicKeyIndex])
                        {
                            return false;
                        }
                    }
                    return true;
                }))
            {
                typeFilter = type => type.Name != "<Module>" && !type.IsInterface && !type.IsAbstract;
            }

            var asmTypes = asm.Types.Where(typeFilter).ToArray();

            foreach (var type in asmTypes)
            {
                var mt = types[type.AssemblyQualifiedName] = ConvertTypeInformation(type);
                typeDefs[mt] = type;
                metadata.AddType("clr-namespace:" + type.Namespace + ";assembly=" + asm.Name, mt);
                var usingNamespace = $"using:{type.Namespace}";
                if (!aliases.TryGetValue(type.Namespace, out var nsAliases))
                {
                    nsAliases = [usingNamespace];
                    aliases[type.Namespace] = nsAliases;
                }
                else if (!nsAliases.Contains(usingNamespace))
                {
                    aliases[type.Namespace] = [.. nsAliases.Union([usingNamespace])];
                }

                foreach (var alias in nsAliases)
                    metadata.AddType(alias, mt);
            }

            // Public interfaces are known by type (so a property typed IResourceDictionary is known to take keyed
            // children, and binding paths can go through interface-typed properties), but they're not elements
            foreach (var type in asm.Types.Where(t => t is { IsInterface: true, IsPublic: true }))
            {
                var mt = types[type.AssemblyQualifiedName] = ConvertTypeInformation(type);
                typeDefs[mt] = type;
            }

            ProcessAvaloniaResources(asm, asmTypes, avaresValues);

            resourceUrls.AddRange(asm.ManifestResourceNames.Where(r => !SkipRes(r)).Select(r => $"resm:{r}?assembly={asm.Name}"));
        }

        var typesByFullName = new Dictionary<string, MetadataType>();
        foreach (var type in types.Values)
            typesByFullName.TryAdd(type.FullName.Length > 0 ? type.FullName : type.Name, type);

        var avaloniaPropertiesByType = new Dictionary<string, HashSet<string>>();
        var at = types.Values.ToArray();
        foreach (var type in at)
        {
            typeDefs.TryGetValue(type, out var typeDef);

            var ctors = typeDef?.Methods
                .Where(m => m is { IsPublic: true, IsStatic: false, Name: ".ctor", Parameters.Count: 1 });

            if (typeDef?.IsEnum ?? false)
            {
                foreach (var value in typeDef.EnumValues)
                {
                    var p = new MetadataProperty(value, type, type, false, true, true, false);

                    type.Properties.Add(p);
                }
            }

            var level = 0;
            typepseudoclasses.Clear();

            type.TemplateParts = [.. (typeDef?.TemplateParts ?? []).Select(item =>
                (Type: types.GetValueOrDefault(item.Type.AssemblyQualifiedName) ?? ConvertTypeInformation(item.Type), item.Name))];

            while (typeDef != null)
            {
                foreach (var pc in typeDef.Pseudoclasses)
                {
                    typepseudoclasses.Add(pc);
                    pseudoclasses.Add(pc);
                }

                var currentType = CollectionExtensions.GetValueOrDefault(types, typeDef.AssemblyQualifiedName);
                // Per declaring type, not per level: every control shares the base classes' fields
                var levelType = typeDef;
                var avaloniaProperties = avaloniaPropertiesByType.GetOrCreate(typeDef.AssemblyQualifiedName,
                    _ => levelType.Fields.Where(IsAvaloniaPropertyField).Select(f => f.Name[..^"Property".Length]).ToHashSet());
                foreach (var prop in typeDef.Properties)
                {
                    if (!prop.IsVisbleTo(targetAssembly))
                        continue;

                    var propertyType = GetType(types, prop.TypeFullName, prop.QualifiedTypeFullName);

                    var p = new MetadataProperty(prop.Name, propertyType,
                        currentType, false, prop.IsStatic, prop.HasPublicGetter,
                        prop.HasPublicSetter || IsSettableFromXaml(typeDef.FullName, prop.Name),
                        IsAvaloniaProperty: avaloniaProperties.Contains(prop.Name),
                        TypeFullName: prop.TypeFullName);

                    type.Properties.Add(p);
                    if (prop.IsContent)
                        type.ContentPropertyName ??= prop.Name;
                }

                foreach (var eventDef in typeDef.Events)
                {
                    var e = new MetadataEvent(eventDef.Name, GetType(types, eventDef.TypeFullName, eventDef.QualifiedTypeFullName),
                        types.GetValueOrDefault(typeDef.FullName, typeDef.AssemblyQualifiedName), false);

                    type.Events.Add(e);
                }

                if (level == 0)
                {
                    foreach (var fieldDef in typeDef.Fields)
                    {
                        if (fieldDef is not { IsStatic: true, IsPublic: true }) continue;
                        if ((fieldDef.IsRoutedEvent || fieldDef.Name.EndsWith("Event", StringComparison.OrdinalIgnoreCase)))
                        {
                            var name = fieldDef.Name;
                            if (fieldDef.Name.EndsWith("Event", StringComparison.OrdinalIgnoreCase))
                            {
                                name = name[..^"Event".Length];
                            }

                            type.Events.Add(new MetadataEvent(name,
                                types.GetValueOrDefault(fieldDef.ReturnTypeFullName, fieldDef.QualifiedTypeFullName),
                                types.GetValueOrDefault(typeDef.FullName, typeDef.AssemblyQualifiedName),
                                true));
                        }
                        else if (fieldDef.Name.EndsWith("Property", StringComparison.OrdinalIgnoreCase)
                                 && fieldDef.ReturnTypeFullName.StartsWith("Avalonia.AttachedProperty`1")
                                )
                        {
                            var name = fieldDef.Name[..^"Property".Length];

                            IMethodInformation? setMethod = null;
                            IMethodInformation? getMethod = null;

                            var setMethodName = $"Set{name}";
                            var getMethodName = $"Get{name}";

                            foreach (var methodDef in typeDef.Methods)
                            {
                                if (methodDef.Name.Equals(setMethodName, StringComparison.OrdinalIgnoreCase)
                                    && methodDef.IsStatic && methodDef is { IsPublic: true, Parameters.Count: 2 })
                                {
                                    setMethod = methodDef;
                                }
                                if (methodDef.IsStatic
                                    && methodDef.Name.Equals(getMethodName, StringComparison.OrdinalIgnoreCase)
                                    && methodDef is { IsPublic: true, Parameters.Count: 1 }
                                    && !string.IsNullOrEmpty(methodDef.ReturnTypeFullName)
                                   )
                                {
                                    getMethod = methodDef;
                                }
                            }

                            if (getMethod is not null)
                            {
                                type.Properties.Add(new MetadataProperty(name,
                                    Type: types.GetValueOrDefault(getMethod.ReturnTypeFullName, getMethod.QualifiedReturnTypeFullName),
                                    DeclaringType: types.GetValueOrDefault(typeDef.FullName, typeDef.AssemblyQualifiedName),
                                    IsAttached: true,
                                    IsStatic: false,
                                    HasGetter: true,
                                    HasSetter: setMethod is not null,
                                    IsAvaloniaProperty: true,
                                    TypeFullName: getMethod.ReturnTypeFullName));
                            }
                        }
                        else if (type.IsStatic)
                        {
                            type.Properties.Add(new MetadataProperty(fieldDef.Name, null, type, false, true, true, false));
                        }
                    }
                }

                if (typeDef.FullName == "Avalonia.AvaloniaObject")
                {
                    type.IsAvaloniaObjectType = true;
                }

                typeDef = typeDef.GetBaseType();
                level++;
            }

            type.HasAttachedProperties = type.Properties.Any(p => p.IsAttached);
            type.HasAttachedEvents = type.Events.Any(e => e.IsAttached);
            type.HasStaticGetProperties = type.Properties.Any(p => p is { IsStatic: true, HasGetter: true });
            type.HasSetProperties = type.Properties.Any(p => p is { IsStatic: false, HasSetter: true });
            if (typepseudoclasses.Count > 0)
            {
                type.HasPseudoClasses = true;
                type.PseudoClasses = [.. typepseudoclasses];
            }

            if (ctors == null) continue;
            {
                var methodInformations = ctors as IMethodInformation[] ?? [.. ctors];
                if (methodInformations?.Any() != true) continue;
                {
                    var supportType = methodInformations.Any(m => m.Parameters[0].TypeFullName == "System.Type");
                    var supportObject = methodInformations.Any(m => m.Parameters[0].TypeFullName is "System.Object" or "System.String");

                    if ((types.TryGetValue(methodInformations.First().Parameters[0].TypeFullName, out var parType)
                         || types.TryGetValue(methodInformations.First().Parameters[0].QualifiedTypeFullName, out parType))
                        && parType.HasHintValues)
                    {
                        type.SupportCtorArgument = MetadataTypeCtorArgument.HintValues;
                        type.HasHintValues = true;
                        type.HintValues = parType.HintValues;
                    }
                    else switch (supportType)
                        {
                            case true when supportObject:
                                type.SupportCtorArgument = MetadataTypeCtorArgument.TypeAndObject;
                                break;
                            case true:
                                type.SupportCtorArgument = MetadataTypeCtorArgument.Type;
                                break;
                            default:
                                {
                                    if (supportObject)
                                        type.SupportCtorArgument = MetadataTypeCtorArgument.Object;
                                    break;
                                }
                        }
                }
            }
        }

        PostProcessTypes(types, metadata, resourceUrls, avaresValues, pseudoclasses);

        return metadata;

        MetadataType? GetType(Dictionary<string, MetadataType> mTypes, params string[] keys)
        {
            MetadataType? type = null;
            foreach (var key in keys)
            {
                if (mTypes.TryGetValue(key, out type))
                {
                    break;
                }
                // Types are keyed by assembly-qualified name, but a Nullable<X> names X by its full name only.
                if (NullableArgument(key) is { } argument
                    && (mTypes.TryGetValue(argument, out var underlying) || typesByFullName.TryGetValue(argument, out underlying)))
                {
                    type = new MetadataType(key)
                    {
                        AssemblyQualifiedName = underlying.AssemblyQualifiedName,
                        FullName = $"{NullablePrefix}<{underlying.FullName}>",
                        IsNullable = true,
                        UnderlyingType = underlying,
                        IsEnum = underlying.IsEnum,
                        HasHintValues = underlying.HasHintValues,
                        HintValues = underlying.HintValues,
                    };
                    mTypes.Add(key, type);
                    break;
                }
            }
            return type;
        }

        bool SkipRes(string res) => ignoredResExt.Any(r => res.EndsWith(r, StringComparison.OrdinalIgnoreCase));
    }

    private static void ProcessAvaloniaResources(IAssemblyInformation asm, ITypeInformation[] asmTypes, List<AvaresInfo> avaresValues)
    {
        const string avaresToken = "Build:"; //or "Populate:" should work both ways

        var resType = asmTypes.FirstOrDefault(t => t.FullName == "CompiledAvaloniaXaml.!AvaloniaResources");
        if (resType != null)
        {
            foreach (var res in resType.Methods.Where(m => m.Name.StartsWith(avaresToken)))
            {
                Registeravares(res.Name.Replace(avaresToken, ""), res.ReturnTypeFullName ?? "");
            }
        }

        //try add avares Embedded resources like image,stream and x:Class
        if (!asm.ManifestResourceNames.Contains("!AvaloniaResources")) return;
        {
            try
            {
                using var avaresStream = asm.GetManifestResourceStream("!AvaloniaResources");
                using var r = new BinaryReader(avaresStream);
                var ms = new MemoryStream(r.ReadBytes(r.ReadInt32()));
                var br = new BinaryReader(ms);

                AvaloniaResourcesIndexEntry[] avaResEntries;

                var version = br.ReadInt32();
                switch (version)
                {
                    case LegacyXmlResourceIndex: // Legacy Xml formart
                        {
                            var assetDoc = XDocument.Load(ms);
                            if (assetDoc.Root is null)
                            {
                                return;
                            }
                            var ns = assetDoc.Root.GetDefaultNamespace();
                            avaResEntries = assetDoc.Root.Element(ns.GetName("Entries"))?.Elements(ns.GetName("AvaloniaResourcesIndexEntry"))
                                .Select(entry => new AvaloniaResourcesIndexEntry(Path: entry.Element(ns.GetName("Path"))?.Value,
                                    Offset: int.Parse(entry.Element(ns.GetName("Offset"))?.Value ?? "0"),
                                    Size: int.Parse(entry.Element(ns.GetName("Size"))?.Value ?? "0")
                                )).ToArray() ?? [];
                            break;
                        }
                    case CurrentResourceIndex: // Binary Formart
                        var entryCount = br.ReadInt32();
                        avaResEntries = new AvaloniaResourcesIndexEntry[entryCount];
                        for (var i = 0; i < entryCount; ++i)
                        {
                            avaResEntries[i] = new(Path: br.ReadString(),
                                Offset: br.ReadInt32(),
                                Size: br.ReadInt32());
                        }
                        break;
                    default:
                        throw new NotSupportedException("Invalid Resource Format");
                }


                if (avaResEntries?.FirstOrDefault(v => v.Path == "/!AvaloniaResourceXamlInfo") is { } xClassEntries)
                {
                    try
                    {
                        avaresStream.Seek(xClassEntries.Offset, SeekOrigin.Current);
                        var xClassDoc = XDocument.Load(new MemoryStream(r.ReadBytes(xClassEntries.Size)));
                        var xClassMappingNode = xClassDoc.Root?.Element(xClassDoc.Root.GetDefaultNamespace().GetName("ClassToResourcePathIndex"));
                        if (xClassMappingNode != null)
                        {
                            const string arraysNs = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";
                            var keyvalueofss = XName.Get("KeyValueOfstringstring", arraysNs);
                            var keyName = XName.Get("Key", arraysNs);
                            var valueName = XName.Get("Value", arraysNs);

                            var xClassMappings = xClassMappingNode.Elements(keyvalueofss)
                                .Where(e => e.Elements(keyName).Any() && e.Elements(valueName).Any())
                                .Select(e => new
                                {
                                    Type = e.Element(keyName)?.Value,
                                    Path = e.Element(valueName)?.Value,
                                }).ToArray();

                            foreach (var xcm in xClassMappings)
                            {
                                var resultType = asmTypes.FirstOrDefault(t => t.FullName == xcm.Type);
                                //if we need another check
                                //if (resultType?.Methods?.Any(m => m.Name == "!XamlIlPopulate") ?? false)
                                if (resultType != null)
                                {
                                    //we set here base class like Style, Styles, UserControl so we can manage
                                    //resources in a common way later
                                    Registeravares(xcm.Path, resultType.GetBaseType()?.FullName ?? "");
                                }
                            }
                        }
                    }
                    catch (Exception xClassEx)
                    {
                        Console.WriteLine($"Failed fetch avalonia x:class resources in {asm.Name}, {xClassEx.Message}");
                    }

                }

                //add other img/stream resources
                if (avaResEntries is not null)
                {
                    foreach (var entry in avaResEntries.Where(v => v.Path is not null && !v.Path.StartsWith("/!")))
                    {
                        Registeravares(entry.Path);
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed fetch avalonia resources in {asm.Name}, {ex.Message}");
            }
        }

        return;

        void Registeravares(string? localUrl, string returnTypeFullName = "")
        {
            if (localUrl is null)
            {
                return;
            }

            var globalUrl = $"avares://{asm.Name}{localUrl}";

            if (avaresValues.Any(v => v.GlobalUrl == globalUrl)) return;
            var avres = new AvaresInfo(asm, returnTypeFullName, localUrl, globalUrl);

            avaresValues.Add(avres);
        }
    }

    private static void ProcessCustomAttributes(IAssemblyInformation asm, Dictionary<string, string[]> aliases)
    {
        foreach (
            var attr in
            asm.CustomAttributes.Where(a => a.TypeFullName == "Avalonia.Metadata.XmlnsDefinitionAttribute"))
        {
            var ns = attr.ConstructorArguments[1].Value?.ToString();
            var val = attr.ConstructorArguments[0].Value?.ToString();
            if (ns is null || val is null)
            {
                continue;
            }

            var current = new[] { val };

            if (aliases.TryGetValue(ns, out var allns))
                allns = [.. allns.Union(current).Distinct()];

            aliases[ns] = allns ?? current;
        }
    }

    private static void PreProcessTypes(Dictionary<string, MetadataType> types, Metadata metadata)
    {
        MetadataType xDataType, xCompiledBindings, boolType, typeType, int32Type;
        var toAdd = new List<MetadataType>
                  {
                      (boolType = new MetadataType(typeof(bool).FullName!)
                      {
                          HasHintValues = true,
                          HintValues = ["True", "False"]
                      }),
                      new("System.Nullable`1<System.Boolean>")
                      {
                          HasHintValues = true,
                          HintValues = ["True", "False"],
                          IsNullable = true,
                          UnderlyingType = boolType,
                      },
                      new(typeof(System.Uri).FullName!),
                      (typeType = new MetadataType(typeof(System.Type).FullName!)),
                      new("Avalonia.Media.IBrush"),
                      new("Avalonia.Media.Imaging.IBitmap"),
                      new("Avalonia.Media.IImage"),
                      (int32Type = new MetadataType(typeof(int).FullName!)
                      {
                          HasHintValues = false,
                      }),
                      new("System.Nullable`1<System.Int32>")
                      {
                          HasHintValues = false,
                          IsNullable = true,
                          UnderlyingType = int32Type,
                      },

                  };

        foreach (var t in toAdd)
            types.Add(t.Name, t);

        var portableXamlExtTypes = new[]
        {
                      new MetadataType("StaticExtension")
                      {
                          SupportCtorArgument = MetadataTypeCtorArgument.Object,
                          HasSetProperties = true,
                          IsMarkupExtension = true,
                      },
                      new MetadataType("TypeExtension")
                      {
                          SupportCtorArgument = MetadataTypeCtorArgument.TypeAndObject,
                          HasSetProperties = true,
                          IsMarkupExtension = true,
                      },
                      new MetadataType("NullExtension")
                      {
                          HasSetProperties = true,
                          IsMarkupExtension = true,
                      },
                      new MetadataType("Class")
                      {
                          IsXamlDirective = true
                      },
                      new MetadataType("Name")
                      {
                          IsXamlDirective = true
                      },
                      new MetadataType("Key")
                      {
                          IsXamlDirective = true
                      },
                      xDataType = new MetadataType("DataType")
                      {
                          IsXamlDirective = true,
                          Properties = { new MetadataProperty("", typeType,null, false, false, false, true)},
                      },
                      xCompiledBindings = new MetadataType("CompileBindings")
                      {
                          IsXamlDirective = true,
                          Properties = { new MetadataProperty("", boolType,null, false, false, false, true)},
                      },
                      new MetadataType("True")
                      {
                          HasSetProperties = true,
                          IsMarkupExtension = true,
                      },
                      new MetadataType("False")
                      {
                          HasSetProperties = true,
                          IsMarkupExtension = true,
                      },
                  };

        // The x: namespace's markup extensions and directives aren't types in any Avalonia assembly
        foreach (var t in portableXamlExtTypes)
        {
            metadata.AddType(Utils.Xaml2006Namespace, t);
        }

        types.Add(xDataType.Name, xDataType);
        types.Add(xCompiledBindings.Name, xCompiledBindings);

        //metadata.AddType("", new MetadataType("xmlns") { IsXamlDirective = true });
    }

    private static void PostProcessTypes(Dictionary<string, MetadataType> types, Metadata metadata,
        IEnumerable<string> resourceUrls, List<AvaresInfo> avaResValues, HashSet<string> pseudoclasses)
    {
        var allresourceUrls = avaResValues.Select(v => v.GlobalUrl).Concat(resourceUrls).ToArray();

        var resType = new MetadataType("avares://,resm:")
        {
            IsStatic = true,
            HasHintValues = true,
            HintValues = allresourceUrls
        };

        types.Add(resType.Name, resType);

        var xamlResType = new MetadataType("avares://*.xaml,resm:*.xaml")
        {
            HasHintValues = true,
            HintValues = [.. resType.HintValues.Where(r => Rhasext(r, ".xaml") || Rhasext(r, ".paml") || Rhasext(r, ".axaml"))]
        };

        var styleResType = new MetadataType("Style avares://*.xaml,resm:*.xaml")
        {
            HasHintValues = true,
            HintValues =

            [
                .. avaResValues.Where(v => v.ReturnTypeFullName.StartsWith("Avalonia.Styling.Style"))
                                                              .Select(v => v.GlobalUrl)
          ,
                          .. resourceUrls.Where(r => Rhasext(r, ".xaml") || Rhasext(r, ".paml") || Rhasext(r, ".axaml")),
                      ]
        };

        types.Add(styleResType.Name, styleResType);

        resType.XamlContextHintValuesFunc = (a, _, _) => FilterLocalRes(xamlResType, a);
        xamlResType.XamlContextHintValuesFunc = (a, _, _) => FilterLocalRes(xamlResType, a);
        styleResType.XamlContextHintValuesFunc = (a, _, _) => FilterLocalRes(styleResType, a);

        types.Add(xamlResType.Name, xamlResType);

        var allProps = new Dictionary<string, MetadataProperty>();

        foreach (var type in types.Where(t => t.Value.IsAvaloniaObjectType))
        {
            foreach (var v in type.Value.Properties.Where(p => p is { IsAvaloniaProperty: true, IsAttached: false }))
            {
                allProps[v.Name] = v;
            }
        }

        // Remmap avalonia base type
        var avaloniaBaseType = new Dictionary<string, MetadataType>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in types)
        {
            if (SAvaloniaBaseType.FirstOrDefault((a, b) => b.StartsWith(a, StringComparison.OrdinalIgnoreCase), kv.Key)
                is not { } at) continue;
            var len = at.Length - 1;
            // First wins: IImage and IBitmap are both hand-made (by name) and real (by assembly-qualified name)
            avaloniaBaseType.TryAdd(at[len] == ',' ? at[..^1] : at, kv.Value);
        }

        string[] allAvaloniaProps = [.. allProps.Keys];

        if (!avaloniaBaseType.TryGetValue("Avalonia.Markup.Xaml.MarkupExtensions.BindingExtension", out var bindingExtType))
        {
            if (avaloniaBaseType.TryGetValue("Avalonia.Data.Binding", out var origBindingType))
            {
                // There's no BindingExtension type: {Binding} creates an Avalonia.Data.Binding
                bindingExtType = origBindingType with
                {
                    Name = "BindingExtension",
                    FullName = "Avalonia.Markup.Xaml.MarkupExtensions.BindingExtension"
                };
                bindingExtType.IsMarkupExtension = true;

                types.Add(bindingExtType.FullName, bindingExtType);
                metadata.AddType(Utils.AvaloniaNamespace, bindingExtType);
            }
        }

        avaloniaBaseType.TryGetValue("Avalonia.Controls.Control", out var controlType);
        types.TryGetValue(typeof(Type).FullName!, out var typeType);

        var dataContextType = new MetadataType("{BindingPath}")
        {
            FullName = "{BindingPath}",
            HasHintValues = true,
            HintValues = ["$parent", "$parent[", "$self"],
        };

        //bindings related hints
        if (types.TryGetValue("Avalonia.Markup.Xaml.MarkupExtensions.BindingExtension", out var bindingType))
        {
            bindingType.SupportCtorArgument = MetadataTypeCtorArgument.None;
            for (var i = 0; i < bindingType.Properties.Count; i++)
            {
                if (bindingType.Properties[i].Name == "Path")
                {
                    bindingType.Properties[i] = bindingType.Properties[i] with
                    {
                        Type = dataContextType
                    };
                }
            }

            bindingType.Properties.Add(new MetadataProperty("", dataContextType, bindingType, false, false, true, true));
        }

        if (avaloniaBaseType.TryGetValue("Avalonia.Data.TemplateBinding", out var templBinding))
        {
            var tbext = new MetadataType("TemplateBindingExtension")
            {
                IsMarkupExtension = true,
                Properties = templBinding.Properties,
                SupportCtorArgument = MetadataTypeCtorArgument.HintValues,
                HasHintValues = allAvaloniaProps?.Any() ?? false,
                HintValues = allAvaloniaProps
            };

            types["TemplateBindingExtension"] = tbext;
            metadata.AddType(Utils.AvaloniaNamespace, tbext);
        }

        // Resource keys come from the project's XAML, at completion time (the theme assemblies don't keep their XAML)
        if (avaloniaBaseType.TryGetValue("Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension", out var dynRes))
        {
            dynRes.SupportCtorArgument = MetadataTypeCtorArgument.HintValues;
            dynRes.HasHintValues = true;
            dynRes.HintsResourceKeys = true;
        }

        if (avaloniaBaseType.TryGetValue("Avalonia.Markup.Xaml.MarkupExtensions.StaticResourceExtension", out var stRes))
        {
            stRes.SupportCtorArgument = MetadataTypeCtorArgument.HintValues;
            stRes.HasHintValues = true;
            stRes.HintsResourceKeys = true;
        }

        //brushes
        if (types.TryGetValue("Avalonia.Media.IBrush", out var brushType) &&
            avaloniaBaseType.TryGetValue("Avalonia.Media.Brushes", out var brushes))
        {
            brushType.HasHintValues = true;
            brushType.HintValues = [.. brushes.Properties.Where(p => p is { IsStatic: true, HasGetter: true }).Select(p => p.Name)];
        }

        //TODO: Remove
        if (avaloniaBaseType.TryGetValue("Avalonia.Styling.Selector", out var styleSelector))
        {
            styleSelector.HasHintValues = true;
            styleSelector.IsCompositeValue = true;

            List<string> hints =
            [
                //some reserved words
                .. new[] { "/template/", ":is()", ">", "#", ".", "^", ":not()" },
                          //some pseudo classes
                          .. pseudoclasses,
                          .. types.Where(t => t.Value.IsAvaloniaObjectType).Select(t => t.Value.Name.Replace(":", "|")),
                      ];

            styleSelector.HintValues = [.. hints];
        }

        string[] bitmaptypes = [".jpg", ".bmp", ".png", ".ico"];

        if (avaloniaBaseType.TryGetValue("Avalonia.Media.Imaging.IBitmap", out var ibitmapType))
        {
            ibitmapType.HasHintValues = true;
            ibitmapType.HintValues = [.. allresourceUrls.Where(Isbitmaptype)];
            ibitmapType.XamlContextHintValuesFunc = (a, _, _) => FilterLocalRes(ibitmapType, a);
        }

        if (avaloniaBaseType.TryGetValue("Avalonia.Media.IImage", out var iImageType))
        {
            iImageType.HasHintValues = true;
            iImageType.HintValues = [.. allresourceUrls.Where(Isbitmaptype)];
            iImageType.XamlContextHintValuesFunc = (a, _, _) => FilterLocalRes(iImageType, a);
        }

        if (avaloniaBaseType.TryGetValue("Avalonia.Controls.WindowIcon", out var winIcon))
        {
            winIcon.HasHintValues = true;
            winIcon.HintValues = [.. allresourceUrls.Where(r => Rhasext(r, ".ico"))];
            winIcon.XamlContextHintValuesFunc = (a, _, _) => FilterLocalRes(winIcon, a);
        }

        if (avaloniaBaseType.TryGetValue("Avalonia.Markup.Xaml.Styling.StyleInclude", out var styleIncludeType))
        {
            var source = styleIncludeType.Properties.FirstOrDefault(p => p.Name == "Source");

            for (var i = 0; i < styleIncludeType.Properties.Count; i++)
            {
                if (styleIncludeType.Properties[i].Name == "Source")
                {
                    styleIncludeType.Properties[i] = styleIncludeType.Properties[i] with
                    {
                        Type = styleResType
                    };
                }
            }
        }

        if (types.TryGetValue("Avalonia.Markup.Xaml.Styling.StyleIncludeExtension", out var styleIncludeExtType))
        {
            for (var i = 0; i < styleIncludeExtType.Properties.Count; i++)
            {
                if (styleIncludeExtType.Properties[i].Name == "Source")
                {
                    styleIncludeExtType.Properties[i] = styleIncludeExtType.Properties[i] with
                    {
                        Type = xamlResType
                    };
                }
            }
        }

        if (types.TryGetValue(typeof(Uri).FullName!, out var uriType))
        {
            uriType.HasHintValues = true;
            uriType.HintValues = [.. allresourceUrls];
            uriType.XamlContextHintValuesFunc = (a, _, _) => FilterLocalRes(uriType, a);
        }

        if (typeType == null) return;
        {
            var typeArguments = new MetadataType("TypeArguments")
            {
                IsXamlDirective = true,
                IsValidForXamlContextFunc = (_, t, _) => t.IsGeneric,
                Properties = { new MetadataProperty("", typeType, null, false, false, false, true) }
            };

            metadata.AddType(Utils.Xaml2006Namespace, typeArguments);
        }

        return;

        IEnumerable<string> FilterLocalRes(MetadataType type, string? currentAssemblyName)
        {
            if (currentAssemblyName is not null)
            {
                var localResPrefix = $"avares://{currentAssemblyName}";
                var resmSuffix = $"?assembly={currentAssemblyName}";

                foreach (var hint in type.HintValues ?? [])
                {
                    if (hint.StartsWith("avares://"))
                    {
                        if (hint.StartsWith(localResPrefix))
                        {
                            yield return hint[localResPrefix.Length..];
                        }
                    }
                    else if (hint.StartsWith("resm:"))
                    {
                        if (hint.EndsWith(resmSuffix))
                        {
                            yield return hint[..^resmSuffix.Length];
                        }
                    }
                }
            }
        }

        bool Isbitmaptype(string resource) => bitmaptypes.Any(ext => Rhasext(resource, ext));

        bool Rhasext(string resource, string ext) => resource.StartsWith("resm:") ? resource.Contains(ext + "?assembly=") : resource.EndsWith(ext);
    }
}
