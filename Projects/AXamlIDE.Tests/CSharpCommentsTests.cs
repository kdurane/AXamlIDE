using AXamlIDE.Services.CSharp;

namespace AXamlIDE.Tests;

public sealed class CSharpCommentsTests
{
    /// <summary>The comment kind at the '|' in <paramref name="code"/>.</summary>
    private static CommentKind At(string code) => CSharpComments.KindAt(code.Replace("|", ""), code.IndexOf('|'));

    [Theory]
    [InlineData("var x = 1; // note|")]
    [InlineData("// note|\nvar x = 1;")]
    [InlineData("/* one\n two| */")]
    [InlineData("/* not closed yet|")]
    [InlineData("//// four slashes|")]
    [InlineData("var s = \"a\"; // after a string|")]
    public void Plain(string code) => Assert.Equal(CommentKind.Plain, At(code));

    [Theory]
    [InlineData("/// <summary>Text|")]
    [InlineData("    /// <|")]
    public void Doc(string code) => Assert.Equal(CommentKind.Doc, At(code));

    [Theory]
    [InlineData("var x = 1;|")]
    [InlineData("// note\nvar x|")]
    [InlineData("/* done */ var x|")]
    [InlineData("var url = \"http://x|")]
    [InlineData("var url = \"http://x\"; var y|")]
    [InlineData("var s = @\"a \"\" // b\"; var y|")]
    [InlineData("var s = \"\"\"\n // raw\n \"\"\"; var y|")]
    [InlineData("var c = '/'; var d = '/'; var y|")]
    [InlineData("var s = \"esc \\\" // b\"; var y|")]
    [InlineData("var @class = 1; var y|")]
    [InlineData("var s = $\"{a} // b\"; var y|")]
    public void None(string code) => Assert.Equal(CommentKind.None, At(code));
}
