using System.Linq;
using AXamlIDE.Services.ColorValues;
using Xunit;

namespace AXamlIDE.Tests;

public class ColorLiteralsTests
{
    private static readonly ArgbColor Red = new(255, 255, 0, 0);

    [Theory]
    [InlineData("#F00", 255, 255, 0, 0)]
    [InlineData("#8F00", 0x88, 255, 0, 0)]
    [InlineData("#FF0000", 255, 255, 0, 0)]
    [InlineData("#80FF0000", 0x80, 255, 0, 0)]
    [InlineData("#00000000", 0, 0, 0, 0)]
    public void Hex_is_read_alpha_first(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(ColorLiterals.TryParseHex(text, out var color));
        Assert.Equal(new ArgbColor(a, r, g, b), color);
    }

    [Fact]
    public void Xaml_finds_hex_and_whole_value_names()
    {
        const string line = """<Border Background="#FF0000" BorderBrush="Blue" Margin="4" Text="Red" />""";
        var found = ColorLiterals.FindInXaml(line);

        Assert.Equal(["#FF0000", "Blue"], found.Select(literal => literal.Text));
        Assert.Equal(line.IndexOf("#FF0000"), found[0].Start);
        Assert.Equal(ColorLiteralKind.Name, found[1].Kind);
        Assert.Equal(new ArgbColor(255, 0, 0, 255), found[1].Color);
    }

    [Fact]
    public void Xaml_finds_element_content_and_skips_entities()
    {
        Assert.Equal(["Red"], ColorLiterals.FindInXaml("<Color x:Key=\"Accent\">Red</Color>").Select(literal => literal.Text));
        Assert.Empty(ColorLiterals.FindInXaml("<TextBlock Text=\"&#123;\" />"));
        Assert.Empty(ColorLiterals.FindInXaml("<Grid ColumnDefinitions=\"Auto,*\" />"));
    }

    [Fact]
    public void CSharp_finds_strings_members_and_calls()
    {
        const string line = """var a = Color.Parse("#336699"); var b = Colors.Red; var c = Brushes.Teal; var d = Color.FromRgb(1, 2, 3); var e = Color.FromArgb(0x80, 0xFF, 0, 0); var f = Color.FromUInt32(0xFF00FF00); var g = Color.Parse("Gold");""";
        var found = ColorLiterals.FindInCSharp(line);

        Assert.Equal(
            ["#336699", "Colors.Red", "Brushes.Teal", "Color.FromRgb(1, 2, 3)", "Color.FromArgb(0x80, 0xFF, 0, 0)", "Color.FromUInt32(0xFF00FF00)", "Gold"],
            found.Select(literal => literal.Text));
        Assert.Equal(new ArgbColor(255, 1, 2, 3), found[3].Color);
        Assert.Equal(new ArgbColor(0x80, 255, 0, 0), found[4].Color);
        Assert.Equal(new ArgbColor(255, 0, 255, 0), found[5].Color);
    }

    [Fact]
    public void CSharp_ignores_unknown_names_and_out_of_range_numbers()
    {
        Assert.Empty(ColorLiterals.FindInCSharp("var x = Colors.NotAColour; var y = Color.FromRgb(300, 0, 0); var z = \"#12\";"));
    }

    [Theory]
    [InlineData("#F00", 255, 0, 0x33, 0x66, "#036")]           // short stays short
    [InlineData("#F00", 255, 0, 0x34, 0x66, "#003466")]        // unless a channel needs two digits
    [InlineData("#f00", 255, 0, 0x33, 0x66, "#036")]           // digits only: case kept below
    [InlineData("#ff0000", 255, 0xAB, 0, 0, "#ab0000")]        // lower case kept
    [InlineData("#FF0000", 0x80, 255, 0, 0, "#80FF0000")]      // alpha added when needed
    [InlineData("#80FF0000", 255, 255, 0, 0, "#FFFF0000")]     // alpha kept when it was there
    public void Hex_keeps_its_style(string original, byte a, byte r, byte g, byte b, string expected)
    {
        var literal = ColorLiterals.FindInXaml($"<Border Background=\"{original}\" />").Single();
        Assert.Equal(expected, ColorLiterals.Format(literal, new ArgbColor(a, r, g, b)));
    }

    [Fact]
    public void Names_stay_names_only_for_named_colours()
    {
        var literal = ColorLiterals.FindInXaml("<Border Background=\"Blue\" />").Single();
        Assert.Equal("Red", ColorLiterals.Format(literal, Red));
        Assert.Equal("#123456", ColorLiterals.Format(literal, new ArgbColor(255, 0x12, 0x34, 0x56)));
        Assert.Equal("Blue", ColorLiterals.Format(literal, literal.Color));
    }

    [Fact]
    public void CSharp_members_and_calls_keep_their_style()
    {
        var line = "Colors.Cyan; Brushes.Red; Color.FromRgb(1, 2, 3); Color.FromRgb(0x01, 0x02, 0x03); Color.FromUInt32(0xFF000000)";
        var found = ColorLiterals.FindInCSharp(line);
        var odd = new ArgbColor(255, 1, 2, 4);
        var seeThrough = new ArgbColor(0x80, 1, 2, 4);

        Assert.Equal("Colors.Cyan", ColorLiterals.Format(found[0], found[0].Color));       // not renamed to Aqua
        Assert.Equal("Color.FromRgb(1, 2, 4)", ColorLiterals.Format(found[0], odd));
        Assert.Equal("Brushes.Lime", ColorLiterals.Format(found[1], new ArgbColor(255, 0, 255, 0)));
        Assert.Equal("new SolidColorBrush(Color.FromRgb(1, 2, 4))", ColorLiterals.Format(found[1], odd));
        Assert.Equal("Color.FromArgb(128, 1, 2, 4)", ColorLiterals.Format(found[2], seeThrough));
        Assert.Equal("Color.FromRgb(0x01, 0x02, 0x04)", ColorLiterals.Format(found[3], odd));
        Assert.Equal("Color.FromUInt32(0x80010204)", ColorLiterals.Format(found[4], seeThrough));
    }
}
