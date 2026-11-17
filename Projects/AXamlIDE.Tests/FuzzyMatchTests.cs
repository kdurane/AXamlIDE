using AXamlIDE.Services.Search;

namespace AXamlIDE.Tests;

public sealed class FuzzyMatchTests
{
    [Theory]
    [InlineData("SearchEverywhereViewModel", "sev")]          // word starts, any case
    [InlineData("SearchEverywhereViewModel", "SeEvVi")]       // carrying on in a word
    [InlineData("SearchEverywhereViewModel", "everyw")]       // inside
    [InlineData("Toggle Comment", "tc")]
    [InlineData("Toggle Comment", "toggle com")]              // every word somewhere
    [InlineData("MainWindow.axaml.cs", "mwax")]
    [InlineData("Show snippets in the completion list", "snip")]
    public void Matches(string name, string query) => Assert.NotNull(FuzzyMatch.Score(name, query));

    [Theory]
    [InlineData("Toggle Comment", "xyz")]
    [InlineData("Toggle Comment", "toggle xyz")]
    [InlineData("Save", "saves")]
    [InlineData("Semantic highlighting", "main")]             // letters merely in order
    [InlineData("Storage", "sae")]
    public void Does_not_match(string name, string query) => Assert.Null(FuzzyMatch.Score(name, query));

    [Fact]
    public void Exact_beats_start_beats_word_starts_beats_inside()
    {
        var exact = FuzzyMatch.Score("Save", "save");
        var start = FuzzyMatch.Score("SaveAll", "save");
        var humps = FuzzyMatch.Score("SaveAllEditors", "sae");
        var inside = FuzzyMatch.Score("Unsaved", "save");      // not at a word start
        Assert.True(exact > start);
        Assert.True(start > humps);
        Assert.True(humps > inside);
    }
}
