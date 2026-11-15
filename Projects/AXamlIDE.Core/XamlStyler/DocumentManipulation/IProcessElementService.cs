// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

using System.Xml.Linq;

namespace AXamlIDE.XamlStyler.DocumentManipulation
{
    public interface IProcessElementService
    {
        void ProcessElement(XElement element);
    }
}