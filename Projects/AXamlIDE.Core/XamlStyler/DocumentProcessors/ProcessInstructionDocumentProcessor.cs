// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

using System;
using System.Text;
using System.Xml;
using AXamlIDE.XamlStyler.Extensions;
using AXamlIDE.XamlStyler.Parser;
using AXamlIDE.XamlStyler.Services;

namespace AXamlIDE.XamlStyler.DocumentProcessors
{
    internal class ProcessInstructionDocumentProcessor : IDocumentProcessor
    {
        private readonly IndentService indentService;

        public ProcessInstructionDocumentProcessor(IndentService indentService)
        {
            this.indentService = indentService;
        }

        public void Process(XmlReader xmlReader, StringBuilder output, ElementProcessContext elementProcessContext)
        {
            elementProcessContext.UpdateParentElementProcessStatus(ContentTypes.Mixed);

            string currentIndentString = this.indentService.GetIndentString(xmlReader.Depth);

            if (!output.IsNewLine())
            {
                output.Append(Environment.NewLine);
            }

            output.Append($"{currentIndentString}<?{xmlReader.Name} {xmlReader.Value}?>");
        }
    }
}