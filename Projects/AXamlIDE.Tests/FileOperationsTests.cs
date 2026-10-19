using System;
using System.IO;
using AXamlIDE.Services.Workspace;

namespace AXamlIDE.Tests;

public sealed class FileOperationsTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private string PathOf(string relative) => Path.Combine(_workspace.Root, relative);

    [Fact]
    public void Rename_a_file_and_a_folder()
    {
        var file = _workspace.Write("a/old.txt", "x");

        var renamed = FileOperations.Rename(file, "new.txt");
        Assert.Equal(PathOf("a/new.txt"), renamed);
        Assert.True(File.Exists(renamed));

        var folder = FileOperations.Rename(PathOf("a"), "b");
        Assert.True(File.Exists(Path.Combine(folder, "new.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void Bad_names_are_refused(string name)
    {
        var file = _workspace.Write("a.txt", "x");
        Assert.Throws<IOException>(() => FileOperations.Rename(file, name));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Renaming_onto_an_existing_name_is_refused()
    {
        var file = _workspace.Write("a.txt", "x");
        _workspace.Write("b.txt", "y");
        Assert.Throws<IOException>(() => FileOperations.Rename(file, "b.txt"));
    }

    [Fact]
    public void Copying_into_the_same_folder_makes_numbered_copies()
    {
        var file = _workspace.Write("Main.cs", "x");

        Assert.Equal(PathOf("Main - Copy.cs"), FileOperations.Paste(file, _workspace.Root, move: false));
        Assert.Equal(PathOf("Main - Copy (2).cs"), FileOperations.Paste(file, _workspace.Root, move: false));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Moving_and_copying_folders()
    {
        _workspace.Write("src/inner/a.txt", "x");
        Directory.CreateDirectory(PathOf("dest"));
        Directory.CreateDirectory(PathOf("other"));

        var copy = FileOperations.Paste(PathOf("src"), PathOf("dest"), move: false);
        Assert.Equal(PathOf("dest/src"), copy);
        Assert.True(File.Exists(Path.Combine(copy, "inner", "a.txt")));
        Assert.True(Directory.Exists(PathOf("src")));

        Assert.Throws<IOException>(() => FileOperations.Paste(PathOf("src"), PathOf("dest"), move: true));   // dest/src exists

        var moved = FileOperations.Paste(PathOf("src"), PathOf("other"), move: true);
        Assert.False(Directory.Exists(PathOf("src")));
        Assert.True(File.Exists(Path.Combine(moved, "inner", "a.txt")));
    }

    [Fact]
    public void A_folder_cant_be_pasted_inside_itself()
    {
        _workspace.Write("src/inner/a.txt", "x");
        Assert.Throws<IOException>(() => FileOperations.Paste(PathOf("src"), PathOf("src/inner"), move: true));
    }

    [Fact]
    public void Cut_and_paste_back_into_the_same_folder_does_nothing()
    {
        var file = _workspace.Write("a.txt", "x");
        Assert.Equal(file, FileOperations.Paste(file, _workspace.Root, move: true));
        Assert.True(File.Exists(file));
    }
}
