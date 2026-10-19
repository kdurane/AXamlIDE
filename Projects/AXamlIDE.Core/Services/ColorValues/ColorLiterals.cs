using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AXamlIDE.Services.ColorValues;

/// <summary>A colour as Avalonia stores it: alpha, red, green, blue.</summary>
public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    public uint ToUInt32() => (uint)(A << 24 | R << 16 | G << 8 | B);

    public static ArgbColor FromUInt32(uint value)
        => new((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
}

/// <summary>How a colour is written, which decides how a new colour is written in its place.</summary>
public enum ColorLiteralKind
{
    /// <summary>#RGB, #ARGB, #RRGGBB or #AARRGGBB (AXAML values, C# strings).</summary>
    Hex,
    /// <summary>A bare name in an AXAML value or a C# string: Red.</summary>
    Name,
    /// <summary>C# Colors.Red.</summary>
    ColorsMember,
    /// <summary>C# Brushes.Red.</summary>
    BrushesMember,
    /// <summary>C# Color.FromRgb(r, g, b).</summary>
    FromRgb,
    /// <summary>C# Color.FromArgb(a, r, g, b).</summary>
    FromArgb,
    /// <summary>C# Color.FromUInt32(0xAARRGGBB).</summary>
    FromUInt32
}

/// <summary>A colour written in a line of code: where (columns in the line), what colour, and how it's written.</summary>
public sealed record ColorLiteral(int Start, int Length, ArgbColor Color, ColorLiteralKind Kind, string Text);

/// <summary>
/// Finds colours in a line of AXAML or C# (for the editor's colour swatches) and writes a new colour in an
/// existing one's place, keeping its style: #RGB stays short when it can, hex keeps its letter case,
/// Color.FromRgb becomes FromArgb only when it needs alpha, and a name stays a name only for a named colour.
/// </summary>
public static partial class ColorLiterals
{
    // Attributes whose values are text, not colours: Text="Red" is the word red.
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Title", "Tag", "ToolTip.Tip", "PlaceholderText", "Watermark", "Name",
        "x:Name", "x:Key", "x:Uid", "AutomationProperties.Name", "AutomationProperties.HelpText", "Classes", "InputGesture"
    };

    [GeneratedRegex(@"(?<![\w#&])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3,4})(?!\w)")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"(?<attr>[\w:.]+)\s*=\s*""\s*(?<name>[A-Za-z]+)\s*""")]
    private static partial Regex XamlNamedAttributePattern();

    [GeneratedRegex(@">\s*(?<name>[A-Za-z]+)\s*</")]
    private static partial Regex XamlNamedContentPattern();

    [GeneratedRegex(@"(?<="")#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3,4})(?="")")]
    private static partial Regex CSharpHexStringPattern();

    [GeneratedRegex(@"\bColor\.Parse\(\s*""(?<name>[A-Za-z]+)""\s*\)")]
    private static partial Regex CSharpParseNamePattern();

    [GeneratedRegex(@"\b(?<type>Colors|Brushes)\.(?<name>[A-Za-z]+)\b")]
    private static partial Regex CSharpMemberPattern();

    [GeneratedRegex(@"\bColor\.(?<method>FromRgb|FromArgb)\(\s*(?<args>(?:(?:0[xX][0-9A-Fa-f]+|\d+)\s*,\s*){2,3}(?:0[xX][0-9A-Fa-f]+|\d+))\s*\)")]
    private static partial Regex CSharpFromPattern();

    [GeneratedRegex(@"\bColor\.FromUInt32\(\s*(?<value>0[xX][0-9A-Fa-f]{1,8}|\d+)[uU]?\s*\)")]
    private static partial Regex CSharpFromUInt32Pattern();

    // ------------------------------------------------------------------ finding

    /// <summary>Colours in a line of AXAML: hex anywhere it isn't an entity, names as whole values.</summary>
    public static IReadOnlyList<ColorLiteral> FindInXaml(string line)
    {
        var found = new List<ColorLiteral>();
        foreach (Match match in HexPattern().Matches(line))
            if (TryParseHex(match.Value, out var color))
                found.Add(new ColorLiteral(match.Index, match.Length, color, ColorLiteralKind.Hex, match.Value));

        foreach (Match match in XamlNamedAttributePattern().Matches(line))
            if (!TextAttributes.Contains(match.Groups["attr"].Value))
                AddName(found, match.Groups["name"], ColorLiteralKind.Name);

        foreach (Match match in XamlNamedContentPattern().Matches(line))
            AddName(found, match.Groups["name"], ColorLiteralKind.Name);

        return Sorted(found);
    }

    /// <summary>Colours in a line of C#: "#hex" strings, Color.Parse("Name"), Colors.X / Brushes.X, Color.FromRgb/FromArgb/FromUInt32.</summary>
    public static IReadOnlyList<ColorLiteral> FindInCSharp(string line)
    {
        var found = new List<ColorLiteral>();
        foreach (Match match in CSharpHexStringPattern().Matches(line))
            if (TryParseHex(match.Value, out var color))
                found.Add(new ColorLiteral(match.Index, match.Length, color, ColorLiteralKind.Hex, match.Value));

        foreach (Match match in CSharpParseNamePattern().Matches(line))
            AddName(found, match.Groups["name"], ColorLiteralKind.Name);

        foreach (Match match in CSharpMemberPattern().Matches(line))
        {
            if (!NamedColors.TryGet(match.Groups["name"].Value, out var color))
                continue;
            var kind = match.Groups["type"].Value == "Colors" ? ColorLiteralKind.ColorsMember : ColorLiteralKind.BrushesMember;
            found.Add(new ColorLiteral(match.Index, match.Length, color, kind, match.Value));
        }

        foreach (Match match in CSharpFromPattern().Matches(line))
        {
            var values = match.Groups["args"].Value.Split(',').Select(arg => ParseNumber(arg.Trim())).ToArray();
            var isArgb = match.Groups["method"].Value == "FromArgb";
            if (values.Length != (isArgb ? 4 : 3) || values.Any(value => value is null or > 255))
                continue;
            var bytes = values.Select(value => (byte)value!.Value).ToArray();
            var color = isArgb ? new ArgbColor(bytes[0], bytes[1], bytes[2], bytes[3]) : new ArgbColor(255, bytes[0], bytes[1], bytes[2]);
            found.Add(new ColorLiteral(match.Index, match.Length, color, isArgb ? ColorLiteralKind.FromArgb : ColorLiteralKind.FromRgb, match.Value));
        }

        foreach (Match match in CSharpFromUInt32Pattern().Matches(line))
            if (ParseNumber(match.Groups["value"].Value) is { } value)
                found.Add(new ColorLiteral(match.Index, match.Length, ArgbColor.FromUInt32((uint)value), ColorLiteralKind.FromUInt32, match.Value));

        return Sorted(found);
    }

    private static void AddName(List<ColorLiteral> found, Group name, ColorLiteralKind kind)
    {
        if (NamedColors.TryGet(name.Value, out var color))
            found.Add(new ColorLiteral(name.Index, name.Length, color, kind, name.Value));
    }

    private static IReadOnlyList<ColorLiteral> Sorted(List<ColorLiteral> found)
        => found.Count < 2 ? found : found.OrderBy(literal => literal.Start).ToList();

    /// <summary>#RGB, #ARGB, #RRGGBB, #AARRGGBB (Avalonia's order: alpha first).</summary>
    public static bool TryParseHex(string text, out ArgbColor color)
    {
        color = default;
        var digits = text.StartsWith('#') ? text[1..] : text;
        if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;

        switch (digits.Length)
        {
            case 3:
            case 4:
                var nibbles = digits.Select(c => (byte)(Convert.ToByte(c.ToString(), 16) * 17)).ToArray();
                color = nibbles.Length == 3
                    ? new ArgbColor(255, nibbles[0], nibbles[1], nibbles[2])
                    : new ArgbColor(nibbles[0], nibbles[1], nibbles[2], nibbles[3]);
                return true;
            case 6:
                color = ArgbColor.FromUInt32(0xFF000000 | value);
                return true;
            case 8:
                color = ArgbColor.FromUInt32(value);
                return true;
            default:
                return false;
        }
    }

    private static long? ParseNumber(string text)
        => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null
            : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    // ------------------------------------------------------------------ writing

    /// <summary>The text to put in place of <paramref name="literal"/> for <paramref name="color"/>, in the same style.</summary>
    public static string Format(ColorLiteral literal, ArgbColor color)
    {
        switch (literal.Kind)
        {
            case ColorLiteralKind.Hex:
                return Hex(color, literal.Text);

            case ColorLiteralKind.Name:
                return NameFor(literal, color) ?? Hex(color, "#");

            case ColorLiteralKind.ColorsMember:
            case ColorLiteralKind.BrushesMember:
                var type = literal.Kind == ColorLiteralKind.ColorsMember ? "Colors" : "Brushes";
                if (NameFor(literal, color) is { } name)
                    return $"{type}.{name}";
                var call = FromCall(color, hex: false);
                return literal.Kind == ColorLiteralKind.ColorsMember ? call : $"new SolidColorBrush({call})";

            case ColorLiteralKind.FromRgb:
            case ColorLiteralKind.FromArgb:
                var openParen = literal.Text.IndexOf('(');
                return FromCall(color, hex: literal.Text[(openParen + 1)..].TrimStart().StartsWith("0x", StringComparison.OrdinalIgnoreCase));

            case ColorLiteralKind.FromUInt32:
                return $"Color.FromUInt32(0x{color.ToUInt32():X8})";

            default:
                throw new ArgumentOutOfRangeException(nameof(literal));
        }
    }

    /// <summary>The original name when the colour didn't change (Cyan stays Cyan), else the colour's name if it has one.</summary>
    private static string? NameFor(ColorLiteral literal, ArgbColor color)
    {
        var originalName = literal.Kind is ColorLiteralKind.Name ? literal.Text : literal.Text[(literal.Text.IndexOf('.') + 1)..];
        return color == literal.Color ? originalName : NamedColors.NameOf(color);
    }

    private static string FromCall(ArgbColor color, bool hex)
    {
        string Number(byte value) => hex ? $"0x{value:X2}" : value.ToString(CultureInfo.InvariantCulture);
        return color.A == 255
            ? $"Color.FromRgb({Number(color.R)}, {Number(color.G)}, {Number(color.B)})"
            : $"Color.FromArgb({Number(color.A)}, {Number(color.R)}, {Number(color.G)}, {Number(color.B)})";
    }

    /// <summary>
    /// #RGB / #ARGB when the original was short and every channel fits in one digit, else #RRGGBB, with alpha
    /// only when it isn't opaque (or the original had it). Lower case if the original was.
    /// </summary>
    private static string Hex(ArgbColor color, string original)
    {
        var digits = original.TrimStart('#');
        var hadAlpha = digits.Length is 4 or 8;
        var withAlpha = hadAlpha || color.A != 255;
        var lower = digits.Any(char.IsLower) && !digits.Any(char.IsUpper);

        static bool Short(byte value) => value % 17 == 0;
        string text;
        if (digits.Length is 3 or 4 && Short(color.R) && Short(color.G) && Short(color.B) && (!withAlpha || Short(color.A)))
            text = (withAlpha ? $"{color.A / 17:X}" : "") + $"{color.R / 17:X}{color.G / 17:X}{color.B / 17:X}";
        else
            text = (withAlpha ? $"{color.A:X2}" : "") + $"{color.R:X2}{color.G:X2}{color.B:X2}";
        return "#" + (lower ? text.ToLowerInvariant() : text);
    }
}
