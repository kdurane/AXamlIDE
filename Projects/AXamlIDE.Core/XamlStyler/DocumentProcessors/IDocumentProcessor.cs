// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

using System.Text;
using System.Xml;

namespace AXamlIDE.XamlStyler.DocumentProcessors
{
    internal interface IDocumentProcessor
    {
        void Process(XmlReader xmlReader, StringBuilder output, ElementProcessContext elementProcessContext);
    }
}