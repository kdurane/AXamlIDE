using AXamlIDE.Services.Mcp;

namespace AXamlIDE.Tests;

public sealed class HoverTextTests
{
    [Fact]
    public void Drops_markdown_escapes_and_nbsp_outside_code()
        => Assert.Equal("Gets an IAsyncRelayCommand wrapping StartViewModel.NewProject().",
            HoverText.Plain(@"Gets an&nbsp;IAsyncRelayCommand&nbsp;wrapping&nbsp;StartViewModel\.NewProject\(\)\."));

    [Fact]
    public void Keeps_code_blocks_as_they_are()
    {
        const string hover = "```csharp\nvoid M(string a\\b)\n```\n  \nSays \\*hello\\*.";
        Assert.Equal("```csharp\nvoid M(string a\\b)\n```\n\nSays *hello*.", HoverText.Plain(hover));
    }

    [Fact]
    public void Keeps_a_backslash_before_letters_and_at_the_end()
        => Assert.Equal(@"C:\temp\", HoverText.Plain(@"C:\temp\"));

    [Fact]
    public void Collapses_runs_of_blank_lines()
        => Assert.Equal("a\n\nb", HoverText.Plain("a\n\n\n\nb\n\n"));
}
