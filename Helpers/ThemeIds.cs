using System;
using System.Linq;
using TextMateSharp.Grammars;

namespace AXamlIDE.Helpers;

    public static class ThemeIds
    {
        public const string AxamlDark = "AxamlDark";

        private static readonly string[] All = [.. Enum.GetNames<ThemeName>(), AxamlDark];

        // Returns the canonical id (case-insensitive match), or null if unknown.
        public static string? Normalize(string id)
            => All.FirstOrDefault(x => string.Equals(x, id.Trim(), StringComparison.OrdinalIgnoreCase));

        public static bool IsKnown(string id) => Normalize(id) is not null;
    }
