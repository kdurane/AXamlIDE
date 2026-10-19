using AXamlIDE.Services.Preview;

namespace AXamlIDE.Tests;

public sealed class PreviewErrorTests
{
    [Fact]
    public void The_position_in_the_message_is_used_and_removed()
    {
        var error = PreviewError.From("xmlns declarations are only allowed on the root element to preserve memory Line 1, position 179.", null, null);

        Assert.Equal("xmlns declarations are only allowed on the root element to preserve memory", error.Message);
        Assert.Equal(1, error.Line);
        Assert.Equal(179, error.Column);
    }

    [Fact]
    public void A_reported_position_wins()
    {
        var error = PreviewError.From("Unable to resolve type Foo (line 3, position 9)", 4, 2);
        Assert.Equal((4, 2), (error.Line, error.Column));
        Assert.Equal("Unable to resolve type Foo", error.Message);
    }

    [Fact]
    public void No_position_at_all()
    {
        var error = PreviewError.From("The preview host crashed.", null, 0);
        Assert.Null(error.Line);
        Assert.Null(error.Column);
    }
}
