// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

using AXamlIDE.XamlStyler.MarkupExtensions.Parser;

namespace AXamlIDE.XamlStyler.Model
{
    public class AttributeInfo
    {
        public AttributeOrderRule OrderRule { get; }

        public string Name { get; }

        public string Value { get; }

        public bool AttributeHasIgnoredNamespace { get; }

        public string AttributeNameWithoutNamespace { get; }

        public MarkupExtension MarkupExtension { get; }

        public bool IsMarkupExtension => MarkupExtension != null;

        public AttributeInfo(string name, string value, bool attributeHasIgnoredNamespace, string attributeNameWithoutNamespace, AttributeOrderRule orderRule, MarkupExtension markupExtension)
        {
            this.Name = name;
            this.Value = value;
            this.AttributeHasIgnoredNamespace = attributeHasIgnoredNamespace;
            this.AttributeNameWithoutNamespace = attributeNameWithoutNamespace;
            this.OrderRule = orderRule;
            this.MarkupExtension = markupExtension;
        }
    }
}