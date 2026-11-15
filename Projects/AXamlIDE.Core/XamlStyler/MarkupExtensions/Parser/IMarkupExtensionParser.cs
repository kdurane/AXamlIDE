// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

namespace AXamlIDE.XamlStyler.MarkupExtensions.Parser
{
    public interface IMarkupExtensionParser
    {
        bool TryParse(string sourceText, out MarkupExtension graph);
    }
}