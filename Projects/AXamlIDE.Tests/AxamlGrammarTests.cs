using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace AXamlIDE.Tests;

/// <summary>The scopes our AXAML grammar (Assets/Grammars/axaml.tmLanguage.json) gives each part of a file.</summary>
public sealed class AxamlGrammarTests
{
    private readonly IGrammar _grammar = new Registry(new GrammarOnly()).LoadGrammar("text.xml.axaml");

    [Fact]
    public void A_multi_line_comment_is_a_comment_all_the_way_through()
    {
        var scopes = Tokenize("<!--", "    Startup project, configuration and play", "-->");

        Assert.All(scopes, token => Assert.Contains("comment.block.axaml", token.Scopes));
        Assert.Contains(scopes, token => token.Text.Contains("Startup project"));
    }

    [Fact]
    public void A_markup_extension_is_split_into_its_parts_and_is_not_a_string()
    {
        var tokens = Tokenize("""<TextBlock Text="{Binding Name, Mode=TwoWay}" />""");

        Assert.Equal("support.class.markup-extension.axaml", ScopeOf(tokens, "Binding"));
        Assert.Equal("variable.other.markup-extension.axaml", ScopeOf(tokens, "Name"));
        Assert.Equal("variable.parameter.markup-extension.axaml", ScopeOf(tokens, "Mode"));
        Assert.Equal("variable.other.markup-extension.axaml", ScopeOf(tokens, "TwoWay"));
        Assert.DoesNotContain(tokens, token => token.Scopes.Any(scope => scope.StartsWith("string.", StringComparison.Ordinal)));
    }

    [Fact]
    public void Tags_attributes_and_directives_have_their_own_scopes()
    {
        var tokens = Tokenize("""<Grid.RowDefinitions x:Name="Rows" Grid.Column="1" xmlns:vm="using:Demo">""");

        Assert.Equal("entity.name.tag.axaml", ScopeOf(tokens, "Grid", first: true));
        Assert.Equal("entity.name.tag.property-element.axaml", ScopeOf(tokens, "RowDefinitions"));
        Assert.Equal("entity.other.attribute-name.directive.axaml", ScopeOf(tokens, "Name"));
        Assert.Equal("entity.other.attribute-name.attached.axaml", ScopeOf(tokens, "Column"));
        Assert.Equal("entity.name.namespace.prefix.axaml", ScopeOf(tokens, "vm"));
        Assert.Equal("string.quoted.double.namespace.axaml", ScopeOf(tokens, "using:Demo"));
    }

    private sealed record Token(string Text, IReadOnlyList<string> Scopes);

    private List<Token> Tokenize(params string[] lines)
    {
        var tokens = new List<Token>();
        IStateStack? state = null;
        foreach (var line in lines)
        {
            var result = _grammar.TokenizeLine(line, state, TimeSpan.FromSeconds(5));
            state = result.RuleStack;
            tokens.AddRange(result.Tokens
                .Select(token => new Token(line[token.StartIndex..Math.Min(line.Length, token.EndIndex)], token.Scopes))
                .Where(token => token.Text.Trim().Length > 0));
        }
        return tokens;
    }

    /// <summary>The innermost scope of the (first or last) token with exactly this text.</summary>
    private static string ScopeOf(List<Token> tokens, string text, bool first = false)
    {
        var matches = tokens.Where(token => token.Text == text).ToList();
        Assert.NotEmpty(matches);
        return (first ? matches[0] : matches[^1]).Scopes[^1];
    }

    private sealed class GrammarOnly : IRegistryOptions
    {
        public IRawGrammar GetGrammar(string scopeName)
        {
            using var reader = new StreamReader(Path.Combine(AppContext.BaseDirectory, "Grammars", "axaml.tmLanguage.json"));
            return GrammarReader.ReadGrammarSync(reader);
        }

        public IRawTheme GetTheme(string scopeName) => null!;
        public ICollection<string> GetInjections(string scopeName) => [];
        public IRawTheme GetDefaultTheme() => new RegistryOptions(ThemeName.DarkPlus).GetDefaultTheme();
    }
}
