// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

using Irony.Parsing;
using System;

namespace AXamlIDE.XamlStyler.MarkupExtensions.Parser
{
    public abstract class Value
    {
        public static Value Create(ParseTreeNode node)
        {
            return LiteralValue.Create(node) ?? (Value)MarkupExtension.Create(node);
        }

        public static implicit operator Value(string value)
        {
            return new LiteralValue(value);
        }

        public Value ToValue()
        {
            throw new NotImplementedException();
        }
    }
}