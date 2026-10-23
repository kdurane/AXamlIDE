using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using AXamlIDE.CompletionEngine.AssemblyMetadata;
using AXamlIDE.CompletionEngine.Parsing;
using AXamlIDE.CompletionEngine.NamespaceTrasformations;
// ReSharper disable PreferConcreteValueOverDefault

namespace AXamlIDE.CompletionEngine.Completion;

public class CompletionEngine
{
    private record struct ElementCompletionInfo(string DisplayText, string InsertText, string? Suffix, int? RecommendedCursorOffset, bool TriggerCompletionAfterInsert);

    public class MetadataHelper
    {
        public Metadata? Metadata { get; private set; }

        public Dictionary<string, string>? Aliases { get; private set; }

        private Dictionary<string, MetadataType>? _types;
        private string? _currentAssemblyName;

        public void SetMetadata(Metadata metadata, string xml, string? currentAssemblyName = null)
        {
            var aliases = GetNamespaceAliases(xml);

            //Check if metadata and aliases can be reused
            if (Metadata == metadata && Aliases != null && _types != null && currentAssemblyName == _currentAssemblyName)
            {
                if (aliases.Count == Aliases.Count)
                {
                    var mismatch = false;
                    foreach (var alias in aliases)
                    {
                        if (Aliases.ContainsKey(alias.Key) && Aliases[alias.Key] == alias.Value) continue;
                        mismatch = true;
                        break;
                    }

                    if (!mismatch)
                        return;
                }
            }
            Aliases = aliases;
            Metadata = metadata;
            _types = null;
            _currentAssemblyName = currentAssemblyName;

            var types = new Dictionary<string, MetadataType>();
            foreach (var alias in Aliases.Concat([new KeyValuePair<string, string>("", "")]))
            {
                var aliasValue = alias.Value ?? "";

                if (!string.IsNullOrEmpty(_currentAssemblyName) && aliasValue.StartsWith("clr-namespace:") && !aliasValue.Contains(";assembly="))
                    aliasValue = $"{aliasValue};assembly={_currentAssemblyName}";

                if (!metadata.Namespaces.TryGetValue(aliasValue, out var ns))
                    continue;

                var prefix = alias.Key.Length == 0 ? "" : alias.Key + ":";
                foreach (var type in ns.Values)
                    types[prefix + type.Name] = type;
            }

            _types = types;
        }

        public IEnumerable<KeyValuePair<string, MetadataType>> FilterTypes(string? prefix, bool withAttachedPropertiesOrEventsOnly = false, bool markupExtensionsOnly = false, bool staticGettersOnly = false, bool xamlDirectiveOnly = false)
        {
            if (_types is null)
            {
                return [];
            }

            prefix ??= "";

            var e = _types
                .Where(t => t.Value.IsXamlDirective == xamlDirectiveOnly && t.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(x => !x.Key.Equals("ControlTemplateResult") && !x.Key.Equals("DataTemplateExtensions"));
            if (withAttachedPropertiesOrEventsOnly)
                e = e.Where(t => t.Value.HasAttachedProperties || t.Value.HasAttachedEvents);
            if (markupExtensionsOnly)
                e = e.Where(t => t.Value.IsMarkupExtension);
            if (staticGettersOnly)
                e = e.Where(t => t.Value.HasStaticGetProperties);

            return e;
        }

        /// <summary>
        /// Types that own attached properties ("Grid" for Grid.Row); with <paramref name="includeEvents"/>, also
        /// non-controls that own attached events ("Gestures" for Gestures.Tapped).
        /// </summary>
        public IEnumerable<string> FilterAttachedOwnerNames(string? prefix, bool includeEvents)
            => FilterTypes(prefix)
                .Where(t => t.Value.HasAttachedProperties
                            || (includeEvents && t.Value is { HasAttachedEvents: true, IsAvaloniaObjectType: false }))
                .Select(t => t.Key);

        public IEnumerable<string> FilterTypeNames(string? prefix, bool withAttachedPropertiesOrEventsOnly = false, bool markupExtensionsOnly = false, bool staticGettersOnly = false, bool xamlDirectiveOnly = false)
        {
            return FilterTypes(prefix, withAttachedPropertiesOrEventsOnly, markupExtensionsOnly, staticGettersOnly, xamlDirectiveOnly).Select(s => s.Key);
        }

        public MetadataType? LookupType(string? name)
        {
            if (name is null)
            {
                return null;
            }

            MetadataType? rv = null;
            if (_types?.TryGetValue(name, out rv) != true)
            {
                // Markup extensions used as XML elements will fail to lookup because
                // the tag name won't include 'Extension'
                _types?.TryGetValue($"{name}Extension", out rv);
            }
            return rv;
        }

        public IEnumerable<string> FilterPropertyNames(string typeName, string? propName,
            bool? attached,
            bool hasSetter,
            bool staticGetter = false)
        {
            var t = LookupType(typeName);
            return MetadataHelper.FilterPropertyNames(t, propName, attached, hasSetter, staticGetter);
        }

        public static IEnumerable<string> FilterPropertyNames(MetadataType? t,
            string? propName,
            bool? attached,
            bool hasSetter,
            bool staticGetter = false)
        {
            return FilterProperty(t, propName, attached, hasSetter, staticGetter).Select(p => p.Name);
        }

        public static IEnumerable<MetadataProperty> FilterProperty(MetadataType? t, string? propName,
            bool? attached,
            bool hasSetter,
            bool staticGetter = false
            )
        {
            propName ??= "";
            if (t == null)
                return [];

            var e = t.Properties.Where(p => p.Name.StartsWith(propName, StringComparison.OrdinalIgnoreCase) && (hasSetter ? p.HasSetter : p.HasGetter));

            if (attached.HasValue)
                e = e.Where(p => p.IsAttached == attached);
            e = staticGetter ? e.Where(p => p is { IsStatic: true, HasGetter: true }) : e.Where(p => !p.IsStatic);

            return e;
        }


        public IEnumerable<string> FilterEventNames(string typeName, string? propName,
            bool attached)
        {
            var t = LookupType(typeName);
            propName ??= "";
            return t == null ? [] : t.Events.Where(n => n.IsAttached == attached && n.Name.StartsWith(propName, StringComparison.OrdinalIgnoreCase)).Select(n => n.Name);
        }

        public MetadataProperty? LookupProperty(string? typeName, string? propName)
            => LookupType(typeName)?.Properties.FirstOrDefault(p => p.Name == propName);
    }

    public MetadataHelper Helper { get; set; } = new MetadataHelper();

    // The document and project of the current GetCompletions call (the engine isn't thread-safe: callers lock).
    private string _fullText = "";
    private XamlProjectInfo _project = XamlProjectInfo.Empty;

    private static readonly string[] DataTypeAttributes = ["x:DataType", "DataType"];

    public static Dictionary<string, string> GetNamespaceAliases(string xml)
    {
        var rv = new Dictionary<string, string>();
        try
        {
            var xmlRdr = XmlReader.Create(new StringReader(xml));
            var result = true;
            while (result && xmlRdr.NodeType != XmlNodeType.Element)
            {
                try
                {
                    result = xmlRdr.Read();
                }
                catch
                {
                    if (xmlRdr.NodeType != XmlNodeType.Element)
                        result = false;
                }
            }

            if (result)
            {
                for (var c = 0; c < xmlRdr.AttributeCount; c++)
                {
                    xmlRdr.MoveToAttribute(c);
                    var ns = xmlRdr.Name;
                    if (ns != "xmlns" && !ns.StartsWith("xmlns:"))
                        continue;
                    ns = ns == "xmlns" ? "" : ns[6..];
                    rv[ns] = xmlRdr.Value;
                }
            }
        }
        catch
        {
            //
        }
        rv.TryAdd("", Utils.AvaloniaNamespace);
        return rv;
    }

    public CompletionSet? GetCompletions(Metadata metadata, string fullText, int pos, string? currentAssemblyName = null, XamlProjectInfo? project = null)
    {
        _fullText = fullText;
        _project = project ?? XamlProjectInfo.Empty;
        var textToCursor = fullText[..pos];
        Helper.SetMetadata(metadata, textToCursor, currentAssemblyName);

        if (Helper.Metadata == null)
            return null;

        if (fullText.Length == 0 || pos == 0)
            return null;
        var state = XmlParser.Parse(textToCursor);

        var completions = new List<Completion>();

        var curStart = state.CurrentValueStart ?? 0;

        switch (state.State)
        {
            case XmlParser.ParserState.StartElement:
                {
                    var tagName = state.TagName;
                    if (tagName is null)
                    {
                    }
                    else if (tagName.StartsWith('/'))
                    {
                        if (textToCursor.Length < 2)
                            return null;
                        var closingState = XmlParser.Parse(textToCursor[..^2]);

                        var name = closingState.GetParentTagName(0);
                        if (name == null)
                            return null;
                        completions.Add(new Completion("/" + name + ">", CompletionKind.Class, priority: 0));
                    }
                    else if (tagName.Contains('.'))
                    {
                        var dotPos = tagName.IndexOf('.', StringComparison.Ordinal);
                        var typeName = tagName[..dotPos];
                        var compName = tagName[(dotPos + 1)..];
                        curStart = curStart + dotPos + 1;

                        var sameType = state.GetParentTagName(1) == typeName;

                        // A property element sets the property, or adds to it when it's a collection (<Grid.RowDefinitions>)
                        completions.AddRange(MetadataHelper.FilterProperty(Helper.LookupType(typeName), compName, attached: sameType ? null : true, hasSetter: false)
                            .Where(p => p.HasSetter || IsCollection(p))
                            .Select(p => p.Name)
                            .Distinct()
                            .Select(p => new Completion(p, sameType ? CompletionKind.Property : CompletionKind.AttachedProperty)));
                    }
                    else
                    {
                        if (tagName.Length == 0)
                        {
                            if (state.GetParentTagName(1) is { } parentTag)
                            {
                                if (!state.IsInClosingTag)
                                {
                                    completions.Add(new Completion("/" + parentTag + ">", CompletionKind.Class, priority: 0));
                                }
                                if (!parentTag.Contains('.'))
                                {
                                    completions.Add(new Completion(parentTag, $"{parentTag}.", CompletionKind.Class, priority: 1)
                                    {
                                        TriggerCompletionAfterInsert = true,
                                    });
                                }
                            }
                            completions.Add(new Completion("!--", "!---->", CompletionKind.Comment) { RecommendedCursorOffset = 3 });
                        }
                        var allowedChildren = AllowedChildTypes(state);
                        completions.AddRange(Helper.FilterTypes(tagName)
                            .Where(kvp => (kvp.Value.CanBeElement || kvp.Value.IsMarkupExtension)
                                          && (allowedChildren is null || allowedChildren.Any(kvp.Value.AssignableTo.Contains)))
                            .Select(kvp =>
                            {
                                var ci = GetElementCompletionInfo(kvp.Key, kvp.Value);
                                return new Completion(ci.DisplayText, ci.InsertText, CompletionKind.Class)
                                {
                                    RecommendedCursorOffset = ci.RecommendedCursorOffset,
                                    TriggerCompletionAfterInsert = ci.TriggerCompletionAfterInsert,
                                };
                            })
                            // {Binding} is both Avalonia.Data.Binding and the BindingExtension made for it
                            .DistinctBy(c => c.InsertText));
                    }

                    break;
                }
            case XmlParser.ParserState.InsideElement:
            case XmlParser.ParserState.StartAttribute:
                {
                    if (state.State == XmlParser.ParserState.InsideElement)
                        curStart = pos; //Force completion to be started from current cursor position

                    var attributeSuffix = "=\"\"";
                    var attributeOffset = 2;
                    if (fullText.Length > pos && fullText[pos] == '=')
                    {
                        // attribute already has value, we are editing name only
                        attributeSuffix = "";
                        attributeOffset = 0;
                    }
                    var attributeName = state.AttributeName;
                    // Attributes already on the element aren't offered again
                    var present = PresentAttributes(fullText, state);
                    if (attributeName?.Contains('.') == true)
                    {
                        var dotPos = attributeName.IndexOf('.');
                        curStart += dotPos + 1;
                        var split = attributeName.Split(['.'], 2);
                        completions.AddRange(Helper.FilterPropertyNames(split[0], split[1], attached: true, hasSetter: true)
                            .Where(x => !present.Contains($"{split[0]}.{x}"))
                            .Select(x => new Completion(x, x + attributeSuffix, x, CompletionKind.AttachedProperty, x.Length + attributeOffset)));

                        completions.AddRange(Helper.FilterEventNames(split[0], split[1], attached: true)
                            .Where(x => !present.Contains($"{split[0]}.{x}"))
                            .Select(v => new Completion(v, v + attributeSuffix, v, CompletionKind.AttachedEvent, v.Length + attributeOffset)));

                        // Classes.primary="{Binding IsPrimary}" turns one class on and off
                        if (split[0] == "Classes")
                        {
                            completions.AddRange(StyleClassNames()
                                .Where(x => !present.Contains($"Classes.{x}"))
                                .Select(x => new Completion(x, x + attributeSuffix, x, CompletionKind.Selector | CompletionKind.Enum, x.Length + attributeOffset)));
                        }
                    }
                    else if (state.TagName is not null)
                    {
                        completions.AddRange(Helper.FilterPropertyNames(state.TagName, attributeName, attached: false, hasSetter: true)
                            .Distinct()
                            .Where(x => !present.Contains(x))
                            .Select(x => new Completion(x, x + attributeSuffix, x, CompletionKind.Property, x.Length + attributeOffset)));

                        completions.AddRange(Helper.FilterEventNames(state.TagName, attributeName, attached: false)
                            .Distinct()
                            .Where(x => !present.Contains(x))
                            .Select(v => new Completion(v, v + attributeSuffix, v, CompletionKind.Event, v.Length + attributeOffset)));

                        var targetType = Helper.LookupType(state.TagName);

                        if (targetType is not null)
                        {
                            completions.AddRange(
                                Helper.FilterTypes(attributeName, xamlDirectiveOnly: true)
                                    .Where(t => !present.Contains(t.Key))
                                    .Where(t => t.Value.Name != "Class" || state.NestingLevel == 1) // x:Class: root only
                                    .Where(t => t.Value.IsValidForXamlContextFunc?.Invoke(currentAssemblyName, targetType, null) ?? true)
                                    .Select(v => new Completion(v.Key, v.Key + attributeSuffix, v.Key, CompletionKind.Class, v.Key.Length + attributeOffset)));

                            if (targetType.IsAvaloniaObjectType)
                            {
                                if (string.IsNullOrEmpty(attributeName) || "xmlns".StartsWith(attributeName, StringComparison.OrdinalIgnoreCase))
                                {
                                    completions.Add(new("xmlns:", CompletionKind.Class));
                                }
                                completions.AddRange(
                                    Helper.FilterAttachedOwnerNames(attributeName, includeEvents: true)
                                        .Select(v => new Completion(v, v + ".", v, CompletionKind.Class) { TriggerCompletionAfterInsert = true }));
                            }
                        }
                    }

                    break;
                }
            case XmlParser.ParserState.AttributeValue:
                {
                    var type = Helper.LookupType(state.TagName);

                    MetadataProperty? prop = null;
                    if (state.AttributeName?.Contains('.') == true)
                    {
                        //Attached property
                        var split = state.AttributeName.Split('.');
                        prop = Helper.LookupProperty(split[0], split[1]);
                    }
                    else if (state.TagName is not null)
                        prop = Helper.LookupProperty(state.TagName, state.AttributeName);

                    //Markup extension, ignore everything else
                    if (state.AttributeValue?.StartsWith('{') == true && state.CurrentValueStart.HasValue)
                    {
                        curStart = state.CurrentValueStart.Value +
                                   BuildCompletionsForMarkupExtension(prop, completions, fullText, state,
                                       textToCursor[state.CurrentValueStart.Value..], currentAssemblyName);
                    }
                    else if (type != null && type.Events.FirstOrDefault(x => x.Name == state.AttributeName) != null)
                    {
                        var name = state.TagName!;
                        // Clean up xmlns
                        var index = name.IndexOf(':');
                        if (index > -1)
                        {
                            name = name.Substring(index + 1, name.Length - index - 1);
                        }
                        completions.Add(new Completion("<New Event Handler>", $"{name}_{state.AttributeName}", CompletionKind.StaticProperty));
                    }
                    else if (state.AttributeName == "Classes" && state.CurrentValueStart is { } classesStart)
                    {
                        // Classes="primary big": the classes the styles use, for the word being typed
                        var value = textToCursor[classesStart..];
                        var wordStart = value.LastIndexOf(' ') + 1;
                        curStart = classesStart + wordStart;
                        var used = value[..wordStart].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        completions.AddRange(StyleClassNames()
                            .Where(c => !used.Contains(c))
                            .Select(c => new Completion(c, CompletionKind.Selector | CompletionKind.Enum)));
                    }
                    else
                    {
                        prop ??= Helper.LookupType(state.AttributeName)?.Properties.FirstOrDefault(p => string.IsNullOrEmpty(p.Name));

                        if (prop?.Type?.HasHintValues == true && state.CurrentValueStart.HasValue)
                        {
                            var search = textToCursor[state.CurrentValueStart.Value..];
                            var hintCompletions = true;
                            if (prop.Type.IsCompositeValue)
                            {
                                // Special case for pseudoclasses within the current edit
                                if (state.AttributeName!.Equals("Selector"))
                                {
                                    hintCompletions = false;
                                    if (SelectorScanner.EndsInStyleClass(search, out var classStart))
                                    {
                                        // "Button.pri": the classes the project's styles use
                                        curStart += classStart;
                                        completions.AddRange(StyleClassNames()
                                            .Select(c => new Completion(c, CompletionKind.Selector | CompletionKind.Enum)));
                                    }
                                    else if (ProcessSelector(search.AsSpan(), state, completions, currentAssemblyName, fullText) is { } delta)
                                    {
                                        curStart += delta;
                                    }
                                }
                                else
                                {
                                    var last = search.Split(' ', ',').Last();
                                    curStart += search.Length - last.Length;
                                    search = last;
                                }
                            }
                            if (hintCompletions)
                            {
                                completions.AddRange(GetHintCompletions(prop.Type, search, currentAssemblyName));
                            }
                        }
                        else if (prop?.Type?.Name == typeof(Type).FullName)
                        {
                            var cKind = CompletionKind.Class;
                            if (state.AttributeName?.Equals("TargetType") == true ||
                                state.AttributeName?.Equals("Selector") == true)
                            {
                                cKind |= CompletionKind.TargetTypeClass;
                            }

                            completions.AddRange(Helper.FilterTypeNames(state.AttributeValue)
                                .Select(x => new Completion(x, x, x, cKind)));
                        }
                        else if ((state.AttributeName == "xmlns" || state.AttributeName?.Contains("xmlns:") == true)
                                 && state.AttributeValue is not null)
                        {
                            IEnumerable<string> FilterNamespaces(Func<string, bool> predicate)
                            {
                                var result = metadata.Namespaces.Keys.Where(predicate).ToList();

                                result.Sort((x, y) => string.Compare(x, y, StringComparison.Ordinal));

                                return result;
                            }

                            var cKind = CompletionKind.Namespace | CompletionKind.VS_XMLNS;

                            if (state.AttributeValue.StartsWith("clr-namespace:"))
                                completions.AddRange(
                                    FilterNamespaces(v => v.StartsWith(state.AttributeValue))
                                        .Select(v => new Completion(v["clr-namespace:".Length..], v, v, cKind)));
                            else
                            {
                                if ("using:".StartsWith(state.AttributeValue))
                                    completions.Add(new Completion("using:", cKind));

                                if ("clr-namespace:".StartsWith(state.AttributeValue))
                                    completions.Add(new Completion("clr-namespace:", cKind));

                                completions.AddRange(
                                    FilterNamespaces(
                                            v =>
                                                v.StartsWith(state.AttributeValue) &&
                                                !v.StartsWith("clr-namespace"))
                                        .Select(v => new Completion(v, cKind)));
                            }
                        }
                        else if (state.AttributeName?.EndsWith(":Class") == true && state.AttributeValue is not null)
                        {
                            if (Helper.Aliases?.TryGetValue(state.AttributeName.Replace(":Class", ""), out var ns) == true && ns == Utils.Xaml2006Namespace)
                            {
                                var asmKey = $";assembly={currentAssemblyName}";
                                var fullClassNames = Helper.Metadata.Namespaces.Where(v => v.Key.EndsWith(asmKey))
                                    .SelectMany(v => v.Value.Values.Where(t => t.IsAvaloniaObjectType))
                                    .Select(v => v.FullName);
                                completions.AddRange(
                                    fullClassNames
                                        .Where(v => v.StartsWith(state.AttributeValue))
                                        .Select(v => new Completion(v, CompletionKind.Class | CompletionKind.TargetTypeClass)));
                            }
                        }
                        else switch (state.TagName)
                            {
                                case "Setter" when state.AttributeName is "Value" or "Property":
                                    {
                                        ProcessStyleSetter(state.AttributeName, state, completions, currentAssemblyName);

                                        var isAttached = textToCursor.AsSpan()[curStart..pos].IndexOf('.') != -1;
                                        if (isAttached)
                                            curStart = pos;
                                        break;
                                    }
                                case "On" when (state.AttributeName?.Equals("Options") == true):
                                    {
                                        var parentTag = state.GetParentTagName(1);
                                        if (parentTag?.Equals("OnPlatform") == true)
                                        {
                                            // Built in types from:
                                            //https://github.com/AvaloniaUI/Avalonia/blob/master/src/Markup/Avalonia.Markup.Xaml/MarkupExtensions/OnPlatformExtension.cs
                                            completions.Add(new Completion("Windows", CompletionKind.Enum));
                                            completions.Add(new Completion("macOS", CompletionKind.Enum));
                                            completions.Add(new Completion("Linux", CompletionKind.Enum));
                                            completions.Add(new Completion("Android", CompletionKind.Enum));
                                            completions.Add(new Completion("iOS", CompletionKind.Enum));
                                            completions.Add(new Completion("Browser", CompletionKind.Enum));
                                        }
                                        else if (parentTag?.Equals("OnFormFactor") == true)
                                        {
                                            completions.Add(new Completion("Desktop", CompletionKind.Enum));
                                            completions.Add(new Completion("Mobile", CompletionKind.Enum));
                                        }

                                        break;
                                    }
                                case "On":
                                    {
                                        if (state.AttributeName?.Equals("Content") == true)
                                        {
                                            // For content, lets find the completions relevant to the property
                                            var propertyTag = state.GetParentTagName(2)!;
                                            var dotPos = propertyTag.IndexOf('.', StringComparison.Ordinal);
                                            var typeName = propertyTag[..dotPos];
                                            var compName = propertyTag[(dotPos + 1)..];

                                            var property = Helper.LookupProperty(typeName, compName);

                                            if (property?.Type?.HasHintValues == true)
                                            {
                                                completions.AddRange(GetHintCompletions(property.Type, null, currentAssemblyName));
                                            }
                                        }

                                        break;
                                    }
                            }
                    }

                    break;
                }
        }

        return completions.Count != 0 ? new CompletionSet() { Completions = SortCompletions(completions), StartPosition = curStart } : null;
    }

    private static List<Completion> SortCompletions(List<Completion> completions)
    {
        // Group the completions based on Kind, and sort the completions for each group
        return [.. completions
                      .GroupBy(i => i.Kind, (kind, compl) =>
                          (Kind: kind, Completions: compl
                          .OrderBy(j => j.Priority).ThenBy(j => j.DisplayText)))
                      .OrderBy(i => GetCompletionPriority(i.Kind))
                      .SelectMany(i => i.Completions)];
    }

    private static int GetCompletionPriority(CompletionKind kind)
    {
        return kind switch
        {
            CompletionKind.MarkupExtension => 0,
            CompletionKind.Namespace => 1,
            CompletionKind.Property => 2,
            CompletionKind.AttachedProperty => 3,
            CompletionKind.StaticProperty => 4,
            CompletionKind.Event => 5,
            CompletionKind.AttachedEvent => 6,
            CompletionKind.Class => 7,
            CompletionKind.Enum => 8,
            CompletionKind.None => 9,
            _ => (int)kind
        };
    }

    private static ElementCompletionInfo GetElementCompletionInfo(string key,
        MetadataType? type)
    {
        var xamlName = key;
        var insertText = xamlName;
        var recommendedCursorOffset = default(int?);
        var triggerCompletionAfterInsert = false;
        if (type is null)
            return new(xamlName, insertText, default, recommendedCursorOffset, triggerCompletionAfterInsert);
        if (type.IsMarkupExtension)
        {
            if (xamlName.EndsWith("extension", StringComparison.OrdinalIgnoreCase))
            {
                xamlName = xamlName[..(key.Length - 9)];
            }
        }
        insertText = xamlName;
        if (!type.IsGeneric)
            return new(xamlName, insertText, default, recommendedCursorOffset, triggerCompletionAfterInsert);
        var tArgsStart = xamlName.IndexOf('`');
        if (tArgsStart <= -1)
            return new(xamlName, insertText, default, recommendedCursorOffset, triggerCompletionAfterInsert);
        var xamlNameBuilder = new System.Text.StringBuilder();
        var insertTextBuilder = new System.Text.StringBuilder();
        xamlNameBuilder.Append(xamlName, 0, tArgsStart);
        insertTextBuilder.Append(xamlName, 0, tArgsStart);
        var args = xamlName[(tArgsStart + 1)..];
        if (!int.TryParse(args
                , System.Globalization.NumberStyles.Number
                , System.Globalization.CultureInfo.InvariantCulture, out var nArgs))
            return new(xamlName, insertText, default, recommendedCursorOffset, triggerCompletionAfterInsert);
        if (nArgs == 1)
        {
            xamlNameBuilder.Append("<T>");
            insertTextBuilder.Append(" x:TypeArguments=\"\"");
            recommendedCursorOffset = insertTextBuilder.Length - 1;
        }
        else
        {
            xamlNameBuilder.Append('<');
            insertTextBuilder.Append(" x:TypeArguments=\"");
            recommendedCursorOffset = insertTextBuilder.Length - 1;
            for (var i = 0; i < nArgs; i++)
            {
                xamlNameBuilder.Append('T');
                xamlNameBuilder.Append(i + 1);
                xamlNameBuilder.Append(',');
                insertTextBuilder.Append(',');
            }
            xamlNameBuilder[^1] = '>';
            insertTextBuilder[^1] = '"';
        }
        xamlName = xamlNameBuilder.ToString();
        insertText = insertTextBuilder.ToString();
        triggerCompletionAfterInsert = true;
        return new(xamlName, insertText, default, recommendedCursorOffset, triggerCompletionAfterInsert);
    }

    private void ProcessStyleSetter(string setterPropertyName, XmlParser state, List<Completion> completions, string? currentAssemblyName)
    {
        var selectorTypeName = ResolveStyleTargetType(state, level: 1);
        if (string.IsNullOrEmpty(selectorTypeName))
            return;

        switch (setterPropertyName)
        {
            case "Property":
                {
                    var value = state.AttributeValue ?? "";

                    if (value.Contains('.'))
                    {
                        var dotPos = value.IndexOf('.');
                        var typeName = value[..dotPos];
                        var compName = value[(dotPos + 1)..];

                        completions.AddRange(Helper.FilterPropertyNames(typeName, compName, attached: true, hasSetter: true)
                            .Select(p => new Completion(p, p, p, CompletionKind.AttachedProperty)));
                    }
                    else
                    {
                        // Setters can only set AvaloniaProperties, not plain CLR ones
                        completions.AddRange(MetadataHelper.FilterProperty(Helper.LookupType(selectorTypeName), value, attached: false, hasSetter: true)
                            .Where(p => p.IsAvaloniaProperty)
                            .Select(p => p.Name)
                            .Distinct()
                            .Select(x => new Completion(x, CompletionKind.Property)));

                        completions.AddRange(Helper.FilterAttachedOwnerNames(value, includeEvents: false)
                            .Select(x => new Completion(x, x + ".", x, CompletionKind.Class) { TriggerCompletionAfterInsert = true }));
                    }

                    break;
                }
            case "Value":
                {
                    var setterProperty = state.FindParentAttributeValue("Property", maxLevels: 0);
                    if (setterProperty is not null)
                    {
                        if (setterProperty.Contains('.'))
                        {
                            var vals = setterProperty.Split('.');
                            selectorTypeName = vals[0];
                            setterProperty = vals[1];
                        }

                        var setterProp = Helper.LookupProperty(selectorTypeName, setterProperty);
                        if (setterProp?.Type?.HasHintValues == true && state.AttributeValue is not null)
                        {
                            completions.AddRange(GetHintCompletions(setterProp.Type, state.AttributeValue, currentAssemblyName));
                        }
                    }

                    break;
                }
        }
    }

    /// <summary>
    /// The type a Style's or ControlTheme's setters apply to, starting from the element <paramref name="level"/>
    /// up (1 = the parent): a ControlTheme's TargetType, or the last type in a Style's Selector. A nested selector
    /// ("^:pointerover") takes its parent style's type. Null when there's no style there.
    /// </summary>
    private static string? ResolveStyleTargetType(XmlParser state, int level)
    {
        for (; state.GetParentTagName(level) is { } tag; level++)
        {
            if (tag == "ControlTheme")
                return TypeNameFromAttribute(state.FindParentAttributeValue("TargetType", level, maxLevels: 0));

            if (tag == "Style")
            {
                var selector = state.FindParentAttributeValue("Selector", level, maxLevels: 0)?.Trim();
                if (string.IsNullOrEmpty(selector))
                    return null;
                if (SelectorScanner.LastTypeName(selector) is { } typeName)
                    return typeName.Replace('|', ':');
                if (!selector.StartsWith('^'))
                    return "Control"; // ".primary" applies to any control
                continue;
            }

            // Property elements (<ControlTheme.Styles>) can sit between a nested style and its parent
            if (!tag.Contains('.'))
                return null;
        }
        return null;
    }

    /// <summary>"vm:Main" from "vm:Main" or "{x:Type vm:Main}".</summary>
    private static string? TypeNameFromAttribute(string? value)
    {
        value = value?.Trim();
        if (value is null || !value.StartsWith('{') || !value.EndsWith('}'))
            return value;
        var inner = value[1..^1].Trim();
        var space = inner.IndexOf(' ');
        if (space < 0)
            return null;
        var typeName = inner[(space + 1)..].Trim();
        return typeName.StartsWith("TypeName=", StringComparison.Ordinal) ? typeName["TypeName=".Length..].Trim() : typeName;
    }

    /// <summary>The x:DataType (or DataTemplate DataType) in effect at the current element.</summary>
    private static string? DataTypeName(XmlParser state)
        => TypeNameFromAttribute(state.FindParentAttributeValue(DataTypeAttributes));

    /// <summary>
    /// What a new child element may be, as full type names: the parent collection's item type (Grid: Control),
    /// its [Content] property's type (Border: Control), or a property element's type (&lt;Button.Background&gt;:
    /// IBrush). Null when anything goes, or it can't be worked out.
    /// </summary>
    private string[]? AllowedChildTypes(XmlParser state)
    {
        if (state.GetParentTagName(1) is not { } parentTag)
            return null;

        MetadataType? target;
        string? targetName;
        var dot = parentTag.IndexOf('.');
        if (parentTag is "Setter" or "Setter.Value")
        {
            // A setter's value has the type of the property it sets
            var setterLevel = parentTag == "Setter" ? 1 : 2;
            var setterProperty = state.FindParentAttributeValue("Property", setterLevel, maxLevels: 0);
            if (setterProperty is null)
                return null;
            var property = setterProperty.Split('.') is [var owner, var attached]
                ? Helper.LookupProperty(owner, attached)
                : Helper.LookupProperty(ResolveStyleTargetType(state, setterLevel + 1), setterProperty);
            (target, targetName) = (property?.Type, property?.TypeFullName);
        }
        else if (dot > 0)
        {
            var property = Helper.LookupProperty(parentTag[..dot], parentTag[(dot + 1)..]);
            (target, targetName) = (property?.Type, property?.TypeFullName);
        }
        else
        {
            if (Helper.LookupType(parentTag) is not { } parentType)
                return null;
            if (parentType.ItemTypeNames.Length > 0)
                return OrAnything(parentType.ItemTypeNames);
            if (parentType.ContentPropertyName is not { } contentName
                || parentType.Properties.FirstOrDefault(p => p.Name == contentName) is not { } content)
                return null;
            (target, targetName) = (content.Type, content.TypeFullName);

            // Content="object" on a control or template (Button, UserControl, DataTemplate): in practice, controls
            if (targetName == "System.Object" && (IsStyleable(parentType) || IsTemplate(parentType)))
                return ["Avalonia.Controls.Control"];
        }

        if (target is { ItemTypeNames.Length: > 0 })
            return OrAnything(target.ItemTypeNames);
        if (CollectionItemTypeFromName(targetName) is { } itemName)
            return OrAnything([itemName]);
        if (target is not null)
            targetName = target.FullName.Length > 0 ? target.FullName : target.Name;
        return targetName is null or "System.Object" ? null : [targetName];

        static string[]? OrAnything(string[] names) => names.Contains("System.Object") ? null : names;
    }

    private static bool IsCollection(MetadataProperty property)
        => property.Type is { ItemTypeNames.Length: > 0 } || CollectionItemTypeFromName(property.TypeFullName) is not null;

    /// <summary>"X" from an interface-typed collection: IList`1&lt;X&gt; or ICollection`1&lt;X&gt;.</summary>
    private static string? CollectionItemTypeFromName(string? typeName)
    {
        if (typeName is null || !typeName.EndsWith('>'))
            return null;
        foreach (var prefix in (string[])["System.Collections.Generic.IList`1<", "System.Collections.Generic.ICollection`1<"])
            if (typeName.StartsWith(prefix, StringComparison.Ordinal))
                return typeName[prefix.Length..^1];
        return null;
    }

    /// <summary>The attributes already set on the element being edited, apart from the one being typed.</summary>
    private static HashSet<string> PresentAttributes(string fullText, XmlParser state)
    {
        var present = new HashSet<string>();
        if (XamlTagScanner.ReadStartTag(fullText, state.ContainingTagStart) is { } tag)
            foreach (var attribute in tag.Attributes)
                if (attribute.NameStart != state.CurrentValueStart)
                    present.Add(attribute.Name);
        return present;
    }

    /// <summary>The style classes used by selectors in this document and the rest of the project.</summary>
    private IEnumerable<string> StyleClassNames()
        => XamlTagScanner.StyleClasses(_fullText).Concat(_project.StyleClasses).Distinct();

    private static bool IsStyleable(MetadataType type) => type.AssignableTo.Contains("Avalonia.StyledElement");

    private static bool IsTemplate(MetadataType type)
        => type.AssignableTo.Any(name => name.StartsWith("Avalonia.Controls.Templates.ITemplate`", StringComparison.Ordinal));

    public IEnumerable<string> FilterHintValues(MetadataType type, string? entered, string? currentAssemblyName, XmlParser? state)
    {
        entered ??= "";

        if (type == null)
            yield break;

        if (type.HintsResourceKeys)
        {
            foreach (var key in XamlTagScanner.ResourceKeys(_fullText).Concat(_project.ResourceKeys).Distinct())
                yield return key;
            yield break;
        }

        if (!string.IsNullOrEmpty(currentAssemblyName) && type.XamlContextHintValuesFunc != null)
        {
            foreach (var v in type.XamlContextHintValuesFunc(currentAssemblyName, type, null).Where(v => v.StartsWith(entered, StringComparison.OrdinalIgnoreCase)))
            {
                yield return v;
            }
        }

        if (type.HintValues is null) yield break;
        {
            // Don't filter values here by 'StartsWith' (old behavior), provide all the hints,
            // For VS, Intellisense will filter the results for us, other users of the completion
            // engine (outside of VS) will need to filter later
            // Otherwise, in VS, it's impossible to get the full list for something like brushes:
            // Background="Red" -> Background="B", will only populate with the 'B' brushes and hitting
            // backspace after that will keep the 'B' brushes only instead of showing the whole list
            // WPF/UWP loads the full list of brushes and highlights starting at the B and then
            // filters the list down from there - otherwise its difficult to keep the completion list
            // and see all choices if making edits
            foreach (var v in type.HintValues)
            {
                yield return v;
            }
        }
    }

    private IEnumerable<Completion> FilterHintValuesForBindingPath(MetadataType bindingPathType, string? entered, string? currentAssemblyName, string? fullText, XmlParser state)
    {
        IEnumerable<Completion> forProperties(string? filterType, string? filter, Func<string, string>? fmtInsertText = null)
                => ForPropertiesFromType(Helper.LookupType(filterType ?? ""), filter, fmtInsertText);

        if (string.IsNullOrEmpty(entered))
            return forProperties(DataTypeName(state), entered);

        var values = entered.Split('.');

        if (values.Length == 1)
        {
            if (values[0].StartsWith("$parent["))
            {
                return Helper.FilterTypes(entered["$parent[".Length..])
                    .Select(v => new Completion(v.Key, $"$parent[{v.Key}].", v.Key, CompletionKind.Class));
            }
            else if (values[0].StartsWith('#'))
            {
                return [.. XamlTagScanner.NamedElements(fullText ?? _fullText).Select(e => new Completion(e.Name, $"#{e.Name}", e.Name, CompletionKind.Class))];
            }

            return forProperties(DataTypeName(state), entered);
        }

        var mdType = WalkBindingPath(values, state, values.Length - 1, out var i);

        return ForPropertiesFromType(mdType, values[i], p => $"{string.Join(".", [.. values.Take(i)])}.{p}");

        IEnumerable<Completion> ForPropertiesFromType(MetadataType? filterType, string? filter, Func<string, string>? fmtInsertText = null)
        {
            if (filterType != null)
            {
                foreach (var propertyName in MetadataHelper.FilterPropertyNames(filterType, filter, false, false))
                {
                    yield return new Completion(propertyName, fmtInsertText?.Invoke(propertyName) ?? propertyName, propertyName, CompletionKind.DataProperty, Priority: 254);
                }
            }
        }
    }

    /// <summary>
    /// The property one segment of a binding path names, e.g. in {Binding Run.StartupProjects} segment 1 is
    /// RunViewModel.StartupProjects. The path starts at the element's (or a parent's) x:DataType, or at
    /// $self / $parent / $parent[Type]. Null when a type on the way isn't known (not built yet, or untyped).
    /// Call <see cref="GetCompletions"/> for the same text first, so <see cref="Helper"/> knows the namespaces.
    /// </summary>
    public (MetadataType Owner, MetadataProperty Property)? ResolveBindingPathSegment(XmlParser state, string path, int segmentIndex)
    {
        var values = path.Split('.');
        if (segmentIndex < 0 || segmentIndex >= values.Length)
            return null;

        var owner = WalkBindingPath(values, state, segmentIndex, out var reached);
        if (owner is null || reached != segmentIndex)
            return null;

        return owner.Properties.FirstOrDefault(p => p.Name == values[segmentIndex]) is { } property
            ? (owner, property)
            : null;
    }

    /// <summary>
    /// Follow a binding path's property types up to (not including) segment <paramref name="upTo"/>; returns the type
    /// that segment is a property of, and in <paramref name="index"/> the segment reached.
    /// </summary>
    private MetadataType? WalkBindingPath(string[] values, XmlParser state, int upTo, out int index)
    {
        string? type;
        int i;

        if (values[0].StartsWith('$'))
        {
            i = 1;
            type = "Control";
            switch (values[0])
            {
                //current control type
                case "$self":
                    type = state.GetParentTagName(0);
                    break;
                //parent control in the xaml
                case "$parent":
                    type = state.GetParentTagName(1) ?? "Control";
                    break;
                default:
                    {
                        if (values[0].StartsWith("$parent[")) //extract parent type
                            type = values[0].Substring("$parent[".Length, values[0].Length - "$parent[".Length - 1);
                        break;
                    }
            }
        }
        else if (values[0].StartsWith('#'))
        {
            // #name: the named element's own type
            i = 1;
            var elementName = values[0][1..];
            type = XamlTagScanner.NamedElements(_fullText).FirstOrDefault(e => e.Name == elementName).TagName ?? "Control";
        }
        else
        {
            i = 0;
            type = DataTypeName(state);
        }

        var mdType = Helper.LookupType(type ?? "");

        while (mdType != null && i < upTo && !string.IsNullOrEmpty(values[i]))
        {
            if (i <= 1 && values[i] == "DataContext")
            {
                //assume parent.datacontext is x:datatype so we have some intellisense
                type = DataTypeName(state);
                mdType = type is not null ? Helper.LookupType(type) : null;
            }
            else
            {
                mdType = mdType.Properties.FirstOrDefault(p => p.Name == values[i])?.Type;
            }
            i++;
        }

        index = i;
        return mdType;
    }

    private List<Completion> GetHintCompletions(MetadataType type, string? entered, string? currentAssemblyName = null, string? fullText = null, XmlParser? state = null)
    {
        var kind = GetCompletionKindForHintValues(type);

        var completions = FilterHintValues(type, entered, currentAssemblyName, state)
            .Select(val => new Completion(val, kind)).ToList();

        if (type.FullName == "{BindingPath}" && state != null)
        {
            completions.AddRange(FilterHintValuesForBindingPath(type, entered, currentAssemblyName, fullText, state));
        }
        return completions;
    }

    private int BuildCompletionsForMarkupExtension(MetadataProperty? property, List<Completion> completions, string fullText, XmlParser state, string data, string? currentAssemblyName)
    {
        int? forcedStart = null;
        var ext = MarkupExtensionParser.Parse(data);

        var transformedName = (ext.ElementName ?? "").Trim();
        if (Helper.LookupType(transformedName)?.IsMarkupExtension != true)
            transformedName += "Extension";

        switch (ext.State)
        {
            case MarkupExtensionParser.ParserStateType.StartElement:
                completions.AddRange(Helper.FilterTypes(ext.ElementName, markupExtensionsOnly: true)
                    .Select(t =>
                    {
                        var name = t.Key.EndsWith("Extension") ? t.Key[..^"Extension".Length] : t.Key;
                        // One that takes arguments ({Binding Path}) gets a space and the list for them straight away;
                        // one that doesn't ({x:Null}) is complete as it is
                        var takesArguments = t.Value.SupportCtorArgument != MetadataTypeCtorArgument.None
                                             || t.Value.Properties.Any(p => p.HasSetter);
                        return takesArguments
                            ? new Completion(name, name + " ", name, CompletionKind.MarkupExtension) { TriggerCompletionAfterInsert = true }
                            : new Completion(name, CompletionKind.MarkupExtension);
                    }));
                break;
            case MarkupExtensionParser.ParserStateType.StartAttribute:
            case MarkupExtensionParser.ParserStateType.InsideElement:
                {
                    if (ext.State == MarkupExtensionParser.ParserStateType.InsideElement)
                        forcedStart = data.Length;

                    var isOnPlatform = ext.ElementName?.Trim().Equals("OnPlatform");
                    var isOnFormFactor = ext.ElementName?.Trim().Equals("OnFormFactor");

                    if ((isOnPlatform == true) || (isOnFormFactor == true))
                    {
                        // If we type a comma after a previous attribute: // {Binding Path=MyProp,
                        // the parser shows that as InsideElement, though we really want that to
                        // be StartAttribute for a list of completions relevant to the markup extension
                        // i.e., above we'd get the completion list for {Binding} again
                        var isActuallyStartAttribute = false;
                        for (var i = data.Length - 1; i >= 0; i--)
                        {
                            if (data[i] == ',')
                            {
                                isActuallyStartAttribute = true;
                                break;
                            }
                            else if (data[i] == '=')
                            {
                                break;
                            }
                        }

                        if (isActuallyStartAttribute || ext.State == MarkupExtensionParser.ParserStateType.StartAttribute)
                        {
                            if (isOnPlatform == true)
                            {
                                completions.Add(new Completion("Windows", "Windows=", "Windows", CompletionKind.Enum));
                                completions.Add(new Completion("macOS", "macOS=", "macOS", CompletionKind.Enum));
                                completions.Add(new Completion("Linux", "Linux=", "Linux", CompletionKind.Enum));
                                completions.Add(new Completion("Android", "Android=", "Android", CompletionKind.Enum));
                                completions.Add(new Completion("iOS", "iOS=", "iOS", CompletionKind.Enum));
                                completions.Add(new Completion("Browser", "Browser=", "Browser", CompletionKind.Enum));
                            }
                            else
                            {
                                completions.Add(new Completion("Desktop", "Desktop=", "Desktop", CompletionKind.Enum));
                                completions.Add(new Completion("Mobile", "Mobile=", "Mobile", CompletionKind.Enum));
                            }
                        }
                        else
                        {
                            var prop = Helper.LookupProperty(state.TagName, state.AttributeName);
                            if (prop?.Type?.HasHintValues == true)
                            {
                                completions.AddRange(GetHintCompletions(prop.Type, null, currentAssemblyName));
                            }
                        }

                        return forcedStart ?? ext.CurrentValueStart;
                    }

                    completions.AddRange(Helper.FilterPropertyNames(transformedName, ext.AttributeName ?? "", attached: false, hasSetter: true)
                        .Where(x => x.Length > 0) // "" is the positional argument ({Binding Path}), not a name to type
                        .Select(x => new Completion(x, x + "=", x, CompletionKind.Property)));

                    var attribName = ext.AttributeName ?? "";
                    var t = Helper.LookupType(transformedName);

                    var ctorArgument = ext.AttributesCount == 0;
                    //skip ctor hints when some property is already set
                    if (t is { IsMarkupExtension: true } && t.SupportCtorArgument != MetadataTypeCtorArgument.None && ctorArgument)
                    {
                        if (t.SupportCtorArgument == MetadataTypeCtorArgument.HintValues)
                        {
                            // {TemplateBinding X}: the properties of the type the template is for
                            if (t.Name == "TemplateBindingExtension"
                                && Helper.LookupType(TypeNameFromAttribute(state.FindParentAttributeValue("TargetType"))) is { } templated)
                            {
                                completions.AddRange(templated.Properties
                                    .Where(p => p is { IsAvaloniaProperty: true, IsAttached: false })
                                    .Select(p => p.Name)
                                    .Distinct()
                                    .Select(p => new Completion(p, CompletionKind.Property)));
                            }
                            else if (t.HasHintValues)
                            {
                                completions.AddRange(GetHintCompletions(t, attribName));
                            }
                        }
                        else if (attribName.Contains('.'))
                        {
                            if (t.SupportCtorArgument != MetadataTypeCtorArgument.Type)
                            {
                                var split = attribName.Split('.');
                                var type = split[0];
                                var prop = split[1];

                                var mType = Helper.LookupType(type);
                                if (mType != null && t.SupportCtorArgument == MetadataTypeCtorArgument.HintValues)
                                {
                                    var hints = FilterHintValues(mType, prop, currentAssemblyName, state);
                                    completions.AddRange(hints.Select(x => new Completion(x, $"{type}.{x}", x, GetCompletionKindForHintValues(mType))));
                                }

                                var props = Helper.FilterPropertyNames(type, prop, attached: false, hasSetter: false, staticGetter: true);
                                completions.AddRange(props.Select(x => new Completion(x, $"{type}.{x}", x, CompletionKind.StaticProperty)));
                            }
                        }
                        else
                        {
                            var types = Helper.FilterTypeNames(attribName,
                                staticGettersOnly: t.SupportCtorArgument == MetadataTypeCtorArgument.Object);

                            completions.AddRange(types.Select(x => new Completion(x, x, x, CompletionKind.Class)));

                            if (property?.Type?.HasHintValues == true)
                            {
                                completions.Add(new Completion(property.Type.Name, property.Type.Name + ".", property.Type.Name, CompletionKind.Class));
                            }
                        }
                    }
                    else
                    {
                        var defaultProp = t?.Properties.FirstOrDefault(p => string.IsNullOrEmpty(p.Name));
                        if (defaultProp?.Type?.HasHintValues ?? false)
                        {
                            completions.AddRange(GetHintCompletions(defaultProp.Type, ext.AttributeName ?? "", currentAssemblyName, fullText, state));
                        }
                    }

                    break;
                }
            case MarkupExtensionParser.ParserStateType.AttributeValue:
            case MarkupExtensionParser.ParserStateType.BeforeAttributeValue:
                {
                    var elementName = ext.ElementName?.Trim();
                    MetadataProperty? prop;
                    if (elementName?.Equals("OnPlatform") == true ||
                        elementName?.Equals("OnFormFactor") == true)
                    {
                        prop = Helper.LookupProperty(state.TagName, state.AttributeName);
                    }
                    else
                    {
                        prop = Helper.LookupProperty(transformedName, ext.AttributeName);
                    }

                    if (prop?.Type?.HasHintValues == true)
                    {
                        var start = data[ext.CurrentValueStart..];
                        completions.AddRange(GetHintCompletions(prop.Type, start, currentAssemblyName, fullText, state));
                    }
                    else if (ext.AttributeName == "ResourceKey" && Helper.LookupType(transformedName) is { HintsResourceKeys: true } resourceExtension)
                    {
                        completions.AddRange(GetHintCompletions(resourceExtension, null, currentAssemblyName));
                    }

                    break;
                }
        }

        return forcedStart ?? ext.CurrentValueStart;
    }

    public static bool ShouldTriggerCompletionListOn(char typedChar)
    {
        return char.IsLetterOrDigit(typedChar) || typedChar == '/' || typedChar == '<'
            || typedChar == ' ' || typedChar == '.' || typedChar == ':' || typedChar == '$'
            || typedChar == '#' || typedChar == '-' || typedChar == '^' || typedChar == '{'
            || typedChar == '=' || typedChar == '[' || typedChar == '|' || typedChar == '(';
    }

    public static CompletionKind GetCompletionKindForHintValues(MetadataType type)
        => type.IsEnum ? CompletionKind.Enum : CompletionKind.StaticProperty;

    public int? ProcessSelector(ReadOnlySpan<char> text, XmlParser state, List<Completion> completions, string? currentAssemblyName, string? fullText)
    {
        int? parsed = default;
        var parser = SelectorParser.Parse(text);
        var previousStatement = parser.PreviousStatement;
        switch (parser.Statement)
        {
            case SelectorStatement.Colon:
            case SelectorStatement.FunctionArgs:
                {
                    var fn = parser.FunctionName;
                    var tn = GetFullName(parser);
                    var isEmptyTn = string.IsNullOrEmpty(tn);
                    if (previousStatement <= SelectorStatement.Middle && isEmptyTn)
                    {
                        completions.Add(new Completion(":is()", ":is(", CompletionKind.Selector | CompletionKind.Enum));
                    }
                    else if (string.IsNullOrEmpty(fn))
                    {
                        completions.Add(new Completion(":not()", ":not(", CompletionKind.Selector | CompletionKind.Enum));
                        completions.Add(new Completion(":nth-child()", ":nth-child(", CompletionKind.Selector | CompletionKind.Enum));
                        completions.Add(new Completion(":nth-last-child()", ":nth-last-child(", CompletionKind.Selector | CompletionKind.Enum));
                    }
                    if (isEmptyTn)
                    {
                        var pseudoClasses = Helper.FilterTypes(default)
                            .Select(kvp => kvp.Value)
                            .Where(m => m.HasPseudoClasses)
                            .SelectMany(m => m.PseudoClasses)
                            .Distinct(StringComparer.OrdinalIgnoreCase);
                        completions.AddRange(pseudoClasses.Select(v => new Completion(v, CompletionKind.Selector | CompletionKind.Enum)));
                    }
                    else
                    {
                        if (Helper.LookupType(tn) is MetadataType { HasPseudoClasses: true } type)
                        {
                            completions.AddRange(type.PseudoClasses.Select(v => new Completion(v, CompletionKind.Selector | CompletionKind.Enum)));
                        }
                    }
                    if (fn == "is")
                    {
                        var types = Helper.FilterTypes(default)
                               .Where(t => IsStyleable(t.Value))
                               .Select(t => t.Value);
                        if (types?.Any() == true)
                        {
                            completions.AddRange(types.Select(v =>
                            {
                                var name = GetXmlnsFullName(v);
                                return new Completion(name, name + ".", CompletionKind.Class | CompletionKind.TargetTypeClass);
                            }));
                        }
                    }
                    if (completions.Count > 0)
                    {
                        parsed = parser.LastParsedPosition ?? 0;
                    }
                }
                break;
            case SelectorStatement.Name:
                {
                    if (parser.IsTemplate)
                    {
                        var ton = parser.TemplateOwner;
                        if (string.IsNullOrEmpty(ton))
                        {
                            ton = GetTypeFromControlTheme();
                        }
                        if (!string.IsNullOrEmpty(ton))
                        {
                            //If it hat TemplateOwner 
                            if (Helper.FilterTypes(ton)
                                .Where(kvp => kvp.Value.TemplateParts.Count > 0)
                                .Select(kvp => kvp.Value)
                                .FirstOrDefault() is { } ownerType)
                            {
                                var parts = ownerType.TemplateParts;
                                var fullName = GetFullName(parser);
                                var partType = string.IsNullOrEmpty(fullName)
                                    ? default
                                    : Helper.FilterTypes(fullName)
                                        .Select(kvp => kvp.Value)
                                        .FirstOrDefault();
                                if (partType is not null)
                                {
                                    parts = [.. parts.Where(p => p.Type.AssemblyQualifiedName == partType.AssemblyQualifiedName)];
                                }
                                if (parts.Count > 0)
                                {
                                    parsed = parser.LastParsedPosition ?? 0;
                                    var x = (parser.LastParsedPosition ?? 0) - parser.LastSegmentStartPosition - 1;
                                    if (string.IsNullOrEmpty(fullName) == false)
                                    {
                                        x += fullName.Length + 1;
                                    }
                                    completions.AddRange(parts.Select(p => new Completion(p.Name, CompletionKind.Name | CompletionKind.Class, p.Type?.Name)
                                    {
                                        RecommendedCursorOffset = (p.Name.Length + (p.Type?.Name.Length > 0 ? p.Type.Name.Length + 3 : 0)),
                                        DeleteTextOffset = -x,
                                    }));
                                }
                            }
                        }
                    }
                    else if (fullText is not null)
                    {
                        var elementName = parser.ElementName;
                        foreach (var (name, _) in XamlTagScanner.NamedElements(fullText).DistinctBy(e => e.Name))
                        {
                            if (!string.IsNullOrEmpty(elementName) && !name.StartsWith(elementName, StringComparison.OrdinalIgnoreCase))
                                continue;
                            parsed = parser.LastParsedPosition ?? 0;
                            completions.Add(new Completion(name, CompletionKind.Name | CompletionKind.Class));
                        }
                    }
                }
                break;
            case SelectorStatement.CanHaveType:
            case SelectorStatement.TypeName:
                {
                    var tn = parser.TypeName;
                    if (GetFullName(parser) is { } typeFullName)
                    {
                        var len = typeFullName.Length;
                        if (len > 0)
                        {
                            if (typeFullName[len - 1] == ':')
                            {
                                var ns = typeFullName[..(len - 1)];

                                if (Helper.Aliases?.TryGetValue(ns, out var ans) == true
                                    && Helper.Metadata?.Namespaces.TryGetValue(ans, out var types) == true)
                                {
                                    IEnumerable<MetadataType> ft = types.Values;
                                    ft = ft
                                        .Where(t => t.IsGeneric == false)
                                        .Where(t => t.IsMarkupExtension == false)
                                        .Where(IsStyleable);
                                    completions.AddRange(ft.Select(v => new Completion(v.Name, $"{ns}|{v.Name}", CompletionKind.Class | CompletionKind.TargetTypeClass)));
                                    parsed = (parser.LastParsedPosition ?? 0) - (tn?.Length ?? 0);
                                }
                            }
                            else if (Helper.FilterTypes(typeFullName).Select(kvp => kvp.Value) is { } types)
                            {
                                types = types
                                        .Where(t => t.IsGeneric == false)
                                        .Where(t => t.IsMarkupExtension == false)
                                        .Where(IsStyleable);
                                completions.AddRange(types.Select(v =>
                                {
                                    var name = GetXmlnsFullName(v);
                                    return new Completion(name, CompletionKind.Class | CompletionKind.TargetTypeClass);
                                }));
                                parsed = (parser.LastParsedPosition ?? 0) - (tn?.Length ?? 0);
                            }
                        }
                    }
                }
                break;
            case SelectorStatement.Property:
                {
                    var typeFullName = GetFullName(parser);
                    if (Helper.LookupType(typeFullName) is { } type)
                    {
                        var propertyName = parser.PropertyName;
                        // [Property=value] matches AvaloniaProperties only
                        var selectorElementProperties = MetadataHelper.FilterProperty(type,
                            propName: propertyName,
                            attached: false,
                            hasSetter: false
                            ).Where(p => p.IsAvaloniaProperty).DistinctBy(p => p.Name).ToList();
                        if (selectorElementProperties.Count > 0)
                        {
                            parsed = text.Length - (propertyName?.Length ?? 0);
                            completions.AddRange(selectorElementProperties.Select(v => new Completion(v.Name, v.Name + "=", v.IsAttached ? CompletionKind.AttachedProperty : CompletionKind.Property)));
                        }
                    }
                }
                break;
            case SelectorStatement.AttachedProperty:
                {
                    var typeFullName = GetFullName(parser);
                    if (Helper.LookupType(typeFullName) is { HasAttachedProperties: true } type)
                    {
                        var propertyName = parser.PropertyName;
                        var selectorElementProperties = MetadataHelper.FilterProperty(type,
                            propName: propertyName,
                            attached: true,
                            hasSetter: false
                            );
                        if (selectorElementProperties?.Any() == true)
                        {
                            // The property name being typed runs to the end of the text
                            parsed = text.Length - (propertyName?.Length ?? 0);
                            completions.AddRange(selectorElementProperties.Select(v => new Completion(v.Name, v.Name + ")", v.IsAttached ? CompletionKind.AttachedProperty : CompletionKind.Property)));
                        }
                    }
                    else
                    {
                        var types = Helper.FilterTypes(default)
                             .Where(t => t.Value.HasAttachedProperties)
                             .Select(t => t.Value);
                        if (types?.Any() == true)
                        {
                            parsed = (parser.LastParsedPosition ?? 0) + 1;
                            completions.AddRange(types.Select(v =>
                            {
                                var name = GetXmlnsFullName(v);
                                return new Completion(name, name + ".", CompletionKind.Class);
                            }));
                        }
                    }
                }
                break;
            case SelectorStatement.Template:
                {
                    completions.Add(new("/template/", "/template/", CompletionKind.Selector | CompletionKind.Enum));
                    parsed = parser.LastParsedPosition;
                }
                break;
            case SelectorStatement.Traversal:
            case SelectorStatement.Start:
                {
                    if (!parser.IsError)
                    {
                        parsed = (parser.LastParsedPosition ?? 0);
                        var parent = state.GetParentTagName(1);
                        // TODO: Crowling Selector operator from Attribute of the Selector
                        completions.Add(new Completion("^", CompletionKind.Selector | CompletionKind.Enum));
                        if (!string.Equals(parent, "ControlTheme", StringComparison.OrdinalIgnoreCase))
                        {
                            completions.Add(new Completion(":", CompletionKind.Selector | CompletionKind.Enum));
                            completions.Add(new Completion(">", CompletionKind.Selector | CompletionKind.Enum));
                            completions.Add(new Completion(".", CompletionKind.Selector | CompletionKind.Enum));
                            completions.Add(new Completion("#", CompletionKind.Selector | CompletionKind.Enum));
                            completions.Add(new Completion(":is()", ":is(", CompletionKind.Selector | CompletionKind.Enum));
                            completions.Add(new Completion(":not()", ":not(", CompletionKind.Selector | CompletionKind.Enum));
                            completions.Add(new Completion(":nth-child()", ":nth-child(", CompletionKind.Selector | CompletionKind.Enum));
                            completions.Add(new Completion(":nth-last-child()", ":nth-last-child(", CompletionKind.Selector | CompletionKind.Enum));
                            completions.Add(new Completion("/template/", "/template/", CompletionKind.Selector | CompletionKind.Enum));
                            var types = Helper.FilterTypes(default)
                                .Where(t => IsStyleable(t.Value))
                                .Select(t => new Completion(t.Value.Name.Replace(":", "|"), CompletionKind.Class | CompletionKind.TargetTypeClass));
                            completions.AddRange(types);
                        }
                    }
                }
                break;
            case SelectorStatement.Value:
                {
                    var typeFullName = GetFullName(parser);
                    if (Helper.LookupType(typeFullName) is { } type)
                    {
                        var propertyName = parser.PropertyName;
                        var prop = MetadataHelper.FilterProperty(type,
                           propName: propertyName,
                           attached: default,
                           hasSetter: false
                           ).FirstOrDefault();
                        var propType = prop?.Type;
                        if (propType?.IsNullable == true)
                        {
                            propType = propType.UnderlyingType;
                        }
                        if (propType is { HasHintValues: true } pt)
                        {
                            var kind = pt.IsEnum
                                ? CompletionKind.Enum
                                : CompletionKind.StaticProperty;
                            IEnumerable<string> values = pt.HintValues!;
                            var value = parser.Value;
                            if (!string.IsNullOrEmpty(value))
                            {
                                values = values
                                    .Where(v => v.StartsWith(value, StringComparison.OrdinalIgnoreCase));
                            }
                            completions.AddRange(values.Select(v => new Completion(v, kind)));
                            parsed = text.Length - (parser.Value?.Length ?? 0);
                        }
                    }
                }
                break;
            case SelectorStatement.Function:
            case SelectorStatement.Class:
            case SelectorStatement.Middle:
            case SelectorStatement.End:
            default:
                break;
        }
        return parsed;

        string GetFullName(SelectorParser parser)
        {
            var ns = parser.Namespace;
            var typename = parser.TypeName;
            if (string.IsNullOrEmpty(typename))
            {
                typename = GetTypeFromControlTheme();
            }
            var typeFullName = string.IsNullOrEmpty(ns)
                ? typename
                : $"{ns}:{typename}";
            return typeFullName ?? string.Empty;
        }

        // "^" in a ControlTheme or nested Style stands for the parent's type
        string? GetTypeFromControlTheme() => ResolveStyleTargetType(state, 1);
    }

    string GetXmlnsFullName(MetadataType type, char namespaceSeparator = '|')
    {
        if (Helper.Metadata?.InverseNamespace.TryGetValue(type.FullName, out var ns) != true
            || string.IsNullOrEmpty(ns)) return type.Name;
        var alias = Helper.Aliases?.FirstOrDefault(a => Equals(a.Value, ns));
        if (alias is not null && !string.IsNullOrEmpty(alias.Value.Key))
        {
            return $"{alias.Value.Key}{namespaceSeparator}{type.Name}";
        }
        return type.Name;
    }

    public static readonly IEnumerable<INamespaceTrasformation> Default =
    [
        new NamespaceTrasformations.ToLowerTrasformation(),
                  new NamespaceTrasformations.ReplaceDot('_'),
              ];

    public static string GetXmlnsFromNamespace(string @namespace) =>
        GetXmlnsFromNamespace(@namespace.ToCharArray(), Default);

    public static string GetXmlnsFromNamespace(char[] @namespace) =>
        GetXmlnsFromNamespace(@namespace, Default);

    public static string GetXmlnsFromNamespace(char[] @namespace, IEnumerable<INamespaceTrasformation> trasformations)
    {
        IEnumerable<char> source = @namespace;
        foreach (var trasformation in trasformations)
        {
            source = trasformation.Apply(source);
        }
        return string.Concat(source);
    }
}
