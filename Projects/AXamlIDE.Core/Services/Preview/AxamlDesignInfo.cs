using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace AXamlIDE.Services.Preview;

/// <summary>
/// What an .axaml file says about how to show it: its x:Class, its design-time DataContext and d:DesignWidth/Height.
/// The compiled app keeps none of the design-time ones (only the previewer, which loads the XAML itself, sees them),
/// so the ui_open tool reads them here and hands them to its host.
///
/// The DataContext is given as text the host can make: "Namespace.Type" (made with its parameterless constructor) from
/// &lt;Design.DataContext&gt;&lt;vm:Type/&gt;&lt;/Design.DataContext&gt;, or "Namespace.Type.Member" from
/// Design.DataContext="{x:Static p:Type.Member}" (or d:DataContext). Anything else (a binding, property values on the
/// element) isn't something the host can rebuild, so it's left out.
/// </summary>
public sealed record AxamlDesignInfo(string? ClassName, string? DataContext, double? Width, double? Height)
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Design = "http://schemas.microsoft.com/expression/blend/2008";

    public static AxamlDesignInfo Read(string xaml)
    {
        XElement root;
        try
        {
            root = XDocument.Parse(xaml).Root ?? throw new XmlException("No root element.");
        }
        catch (XmlException)
        {
            return new AxamlDesignInfo(null, null, null, null);
        }

        return new AxamlDesignInfo(
            (string?)root.Attribute(Xaml + "Class"),
            DataContextOf(root),
            Number(root.Attribute(Design + "DesignWidth")),
            Number(root.Attribute(Design + "DesignHeight")));
    }

    private static string? DataContextOf(XElement root)
    {
        var attribute = root.Attributes().FirstOrDefault(a => a.Name.LocalName == "Design.DataContext")
                        ?? root.Attribute(Design + "DataContext");
        if (attribute is not null)
            return StaticMember(root, attribute.Value);

        var element = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Design.DataContext")
                      ?? root.Elements().FirstOrDefault(e => e.Name == Design + "DataContext");
        return element?.Elements().FirstOrDefault() is { } made ? TypeName(made.Name) : null;
    }

    /// <summary>"{x:Static p:Type.Member}" (or Member=...) → "Namespace.Type.Member"; null for any other markup.</summary>
    private static string? StaticMember(XElement scope, string value)
    {
        value = value.Trim();
        if (!value.StartsWith('{') || !value.EndsWith('}'))
            return null;
        var parts = value[1..^1].Split((char[])[' ', '\t', '\r', '\n'], 2, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] is not ("x:Static" or "Static"))
            return null;

        var member = parts[1].Trim();
        if (member.StartsWith("Member", System.StringComparison.Ordinal) && member.IndexOf('=') is var equals and > 0)
            member = member[(equals + 1)..].Trim();
        var colon = member.IndexOf(':');
        if (colon < 0)
            return null;   // a type in the Avalonia namespace: not the app's
        var ns = scope.GetNamespaceOfPrefix(member[..colon]);
        return ns is null ? null : ClrNamespace(ns) is { } clr ? clr + "." + member[(colon + 1)..] : null;
    }

    private static string? TypeName(XName name) => ClrNamespace(name.Namespace) is { } clr ? clr + "." + name.LocalName : null;

    /// <summary>"using:A.B" or "clr-namespace:A.B;assembly=X" → "A.B".</summary>
    private static string? ClrNamespace(XNamespace ns)
    {
        var text = ns.NamespaceName;
        foreach (var prefix in (string[])["using:", "clr-namespace:"])
        {
            if (text.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                var name = text[prefix.Length..];
                var end = name.IndexOf(';');
                return (end >= 0 ? name[..end] : name).Trim();
            }
        }
        return null;
    }

    private static double? Number(XAttribute? attribute)
        => attribute is not null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
}
