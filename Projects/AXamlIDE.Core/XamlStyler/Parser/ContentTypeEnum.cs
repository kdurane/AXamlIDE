// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

using System;

namespace AXamlIDE.XamlStyler.Parser
{
    [Flags]
    public enum ContentTypes
    {
        None = 0,
        SingleLineTextOnly = 1,
        MultiLineTextOnly = 2,
        Mixed = 4,
    }
}