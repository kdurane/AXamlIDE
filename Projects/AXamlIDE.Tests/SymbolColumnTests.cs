using AXamlIDE.Services.Mcp;

namespace AXamlIDE.Tests;

public sealed class SymbolColumnTests
{
    [Fact]
    public void Finds_the_whole_name_not_part_of_a_longer_one()
        => Assert.Equal(24, SymbolColumn.Find("var FileName = document.Name;", "Name"));

    [Fact]
    public void Falls_back_to_part_of_a_name_when_that_is_all_there_is()
        => Assert.Equal(8, SymbolColumn.Find("var FileName = 1;", "Name"));

    [Fact]
    public void Takes_the_first_whole_occurrence_without_a_column()
        => Assert.Equal(0, SymbolColumn.Find("value = value + 1;", "value"));

    [Fact]
    public void Takes_the_occurrence_closest_to_the_column()
        => Assert.Equal(8, SymbolColumn.Find("value = value + 1;", "value", near: 10));

    [Fact]
    public void Is_null_when_the_name_is_not_on_the_line()
    {
        Assert.Null(SymbolColumn.Find("int x = 1;", "y"));
        Assert.Null(SymbolColumn.Find("int x = 1;", ""));
    }

    [Fact]
    public void Underscores_and_digits_are_part_of_names()
        => Assert.Equal(9, SymbolColumn.Find("_name2 = name; name", "name"));
}
