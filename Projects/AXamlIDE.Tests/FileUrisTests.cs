using System;
using System.IO;
using AXamlIDE.Services.Lsp;

namespace AXamlIDE.Tests;

public class FileUrisTests
{
    [Theory]
    [InlineData("file:///c:/src/App/Program.cs", @"C:\src\App\Program.cs")]
    [InlineData("file:///C:/src/App/Program.cs", @"C:\src\App\Program.cs")]
    [InlineData("file:///c%3A/src/App/Program.cs", @"C:\src\App\Program.cs")]   // VS Code / Roslyn's spelling
    [InlineData("file:///c%3a/Users/Me/My%20App/a.cs", @"C:\Users\Me\My App\a.cs")]
    [InlineData("file://server/share/a.cs", @"\\server\share\a.cs")]
    [InlineData("file:///c:/src/a.cs#L10", @"C:\src\a.cs")]
    public void Reads_windows_uris(string uri, string expected)
        => Assert.Equal(expected, FileUris.ToPath(uri, windows: true));

    [Theory]
    [InlineData("file:///home/me/app/Main.cs", "/home/me/app/Main.cs")]
    [InlineData("file:///home/me/My%20App/a.cs", "/home/me/My App/a.cs")]
    [InlineData("file://localhost/home/me/a.cs", "/home/me/a.cs")]
    public void Reads_unix_uris(string uri, string expected)
        => Assert.Equal(expected, FileUris.ToPath(uri, windows: false));

    [Theory]
    [InlineData("roslyn-source-generated://App/Gen.g.cs")]
    [InlineData("untitled:Untitled-1")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_schemes_are_not_files(string? uri) => Assert.Null(FileUris.ToPath(uri));

    [Fact]
    public void A_path_round_trips_through_its_uri()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "My App", "a b.cs"));
        Assert.Equal(path, FileUris.ToPath(FileUris.FromPath(path)));
    }

    [Fact]
    public void Different_spellings_of_one_file_are_the_same_file()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.True(FileUris.SameFile("file:///c:/src/a.cs", "file:///C%3A/src/a.cs"));
        Assert.False(FileUris.SameFile("file:///c:/src/a.cs", "file:///c:/src/b.cs"));
    }
}
