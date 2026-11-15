// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

namespace AXamlIDE.XamlStyler.MarkupExtensions.Parser
{
    internal class StringTerminal : MemberNameOrStringTerminal
    {
        public StringTerminal(string name) : base(name)
        {
        }

        protected override bool IsMemberName => false;
    }
}