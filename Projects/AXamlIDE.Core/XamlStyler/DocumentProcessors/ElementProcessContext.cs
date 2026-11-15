// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

using System.Collections.Generic;
using AXamlIDE.XamlStyler.Parser;

namespace AXamlIDE.XamlStyler.DocumentProcessors
{
    public class ElementProcessContext
    {
        private readonly Stack<ElementProcessStatus> elementProcessStatusStack;

        public int Count => this.elementProcessStatusStack.Count;

        public ElementProcessStatus Current => this.elementProcessStatusStack.Peek();

        public ElementProcessContext()
        {
            this.elementProcessStatusStack = new Stack<ElementProcessStatus>();
            this.elementProcessStatusStack.Push(new ElementProcessStatus());
        }

        public void Push(ElementProcessStatus elementProcessStatus)
        {
            this.elementProcessStatusStack.Push(elementProcessStatus);
        }

        public ElementProcessStatus Pop()
        {
            return this.elementProcessStatusStack.Pop();
        }

        public void UpdateParentElementProcessStatus(ContentTypes contentType)
        {
            ElementProcessStatus parentElementProcessStatus = this.elementProcessStatusStack.Peek();
            parentElementProcessStatus.ContentType |= contentType;
        }
    }
}