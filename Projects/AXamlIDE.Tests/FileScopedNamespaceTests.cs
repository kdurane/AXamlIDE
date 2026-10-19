using System.Linq;
using AXamlIDE.Services.CSharp;

namespace AXamlIDE.Tests;

public sealed class FileScopedNamespaceTests
{
    /// <summary>Type ';' at the marker '|' and apply the conversion (null when it doesn't apply).</summary>
    private static string? TypeSemicolon(string before)
    {
        var at = before.IndexOf('|');
        var text = before.Remove(at, 1).Insert(at, ";");
        var edits = FileScopedNamespace.ConvertAfterSemicolon(text, at);
        if (edits is null)
            return null;
        foreach (var edit in edits)   // listed from the end of the text
            text = text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.Replacement);
        return text;
    }

    [Fact]
    public void Block_namespace_becomes_file_scoped()
    {
        const string before = "using System;\n\nnamespace App.Views|\n{\n    public class A\n    {\n        void M() { }\n    }\n}\n";
        const string after = "using System;\n\nnamespace App.Views;\n\npublic class A\n{\n    void M() { }\n}\n";
        Assert.Equal(after, TypeSemicolon(before));
    }

    [Fact]
    public void Brace_on_the_same_line_and_crlf()
    {
        const string before = "namespace App| {\r\n    class A { }\r\n}\r\n";
        Assert.Equal("namespace App;\r\n\r\nclass A { }\r\n", TypeSemicolon(before));
    }

    [Fact]
    public void Braces_in_strings_and_comments_are_skipped()
    {
        const string before = "namespace App|\n{\n    class A\n    {\n        string s = \"}\"; // }\n        char c = '}';\n        /* } */\n        string v = @\"\"\"}\";\n    }\n}\n";
        var after = TypeSemicolon(before);
        Assert.NotNull(after);
        Assert.EndsWith("    string v = @\"\"\"}\";\n}\n", after);
        Assert.StartsWith("namespace App;\n\nclass A\n", after);
    }

    [Fact]
    public void Lines_that_arent_all_indented_are_left_as_they_are()
    {
        const string before = "namespace App|\n{\n    class A { }\nclass B { }\n}\n";
        Assert.Equal("namespace App;\n\n    class A { }\nclass B { }\n", TypeSemicolon(before));
    }

    [Theory]
    [InlineData("namespace App|\n{\n    class A { }\n}\nnamespace Other { }\n")]   // something after the namespace
    [InlineData("namespace App|\nclass A { }\n")]                                   // no '{' next
    [InlineData("var x = 1|\n{\n}\n")]                                              // not a namespace line
    [InlineData("namespace App|\n{\n    class A {\n")]                               // no matching '}'
    public void Doesnt_apply(string before) => Assert.Null(TypeSemicolon(before));
}
