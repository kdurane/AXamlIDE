using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace AXamlIDE.Services.Preview;

/// <summary>
/// Makes a XAML document preview in the Light or Dark theme (the preview's theme drop-down), without
/// changing how it loads:
///
///   Window, Application, ThemeVariantScope   RequestedThemeVariant is set on the root
///   UserControl and other content controls   the single content element is wrapped:
///                                            &lt;ThemeVariantScope RequestedThemeVariant="Dark"&gt;&lt;Border Background=...&gt;content
///   anything else (Styles, a Panel with several children...)   left as it is
///
/// The root itself is never wrapped: its xmlns declarations and x:Class must stay on the root element.
/// </summary>
public static class PreviewThemeVariant
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    /// <summary>The document to preview in <paramref name="theme"/> ("Light" or "Dark"); unchanged for any other value.</summary>
    public static string Apply(string xaml, string theme)
    {
        if (theme is not ("Light" or "Dark"))
            return xaml;

        XDocument document;
        try
        {
            document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return xaml;   // the preview reports the error itself
        }

        if (document.Root is not { } root)
            return xaml;

        if (root.Name.LocalName is "Window" or "Application" or "ThemeVariantScope")
        {
            root.SetAttributeValue("RequestedThemeVariant", theme);
            return document.ToString(SaveOptions.DisableFormatting);
        }

        // Content is what isn't a property element (UserControl.Styles, Design.DataContext...).
        var content = root.Elements().Where(element => !element.Name.LocalName.Contains('.')).ToList();
        if (content.Count != 1 || root.Name.LocalName is "Styles" or "Style" or "ResourceDictionary")
            return xaml;

        var child = content[0];
        child.ReplaceWith(new XElement(Avalonia + "ThemeVariantScope",
            new XAttribute("RequestedThemeVariant", theme),
            new XElement(Avalonia + "Border",
                new XAttribute("Background", "{DynamicResource SystemControlBackgroundAltHighBrush}"),
                child)));   // child is copied into the new element (it still has a parent)
        return document.ToString(SaveOptions.DisableFormatting);
    }
}
