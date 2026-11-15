// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

using System.Collections.Generic;

namespace AXamlIDE.XamlStyler.Options
{
    public class XamlLanguageOptions
    {
        public bool IsFormatable { get; set; }

        public HashSet<char> UnescapedAttributeCharacters { get; } = new HashSet<char>();
    }
}