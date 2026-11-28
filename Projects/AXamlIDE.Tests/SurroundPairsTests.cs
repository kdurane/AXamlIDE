using AXamlIDE.Services.Typing;

namespace AXamlIDE.Tests;

public sealed class SurroundPairsTests
{
    [Theory]
    [InlineData('"', '"')]
    [InlineData('\'', '\'')]
    [InlineData('`', '`')]
    [InlineData('(', ')')]
    [InlineData('[', ']')]
    [InlineData('{', '}')]
    public void QuotesAndOpeningBracketsSurround(char typed, char closing)
        => Assert.Equal(closing, SurroundPairs.ClosingFor(typed));

    [Theory]
    [InlineData('<')]   // replacing a selected '>' with '<' must still work
    [InlineData(')')]
    [InlineData('a')]
    [InlineData(';')]
    public void OtherCharactersReplaceTheSelection(char typed)
        => Assert.Null(SurroundPairs.ClosingFor(typed));
}
