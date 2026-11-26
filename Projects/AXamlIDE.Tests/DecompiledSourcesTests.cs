using System.IO;
using AXamlIDE.Services.Debugging;

namespace AXamlIDE.Tests;

public class DecompiledSourcesTests
{
    [Theory]
    [InlineData("decompiled/System.Linq/cc5dcd19-6274-47e3-a6da-049851cb47ea/System.Linq/Enumerable.cs", true)]
    [InlineData(@"decompiled\System.Linq\cc5dcd19\Enumerable.cs", true)]
    [InlineData("/home/user/App/Program.cs", false)]
    [InlineData("src/decompiled/Program.cs", false)]
    [InlineData("decompiled/Program.cs", false)]
    [InlineData(null, false)]
    public void Knows_the_debuggers_made_up_paths(string? path, bool decompiled)
        => Assert.Equal(decompiled, DecompiledSources.IsDecompiledPath(path));

    [Fact]
    public void Its_files_are_the_ones_under_its_folder()
    {
        Assert.True(DecompiledSources.IsInside(Path.Combine(DecompiledSources.Folder, "decompiled", "A", "1", "T.cs")));
        Assert.False(DecompiledSources.IsInside(Path.Combine(Path.GetTempPath(), "T.cs")));
        Assert.False(DecompiledSources.IsInside(DecompiledSources.Folder + "Other" + Path.DirectorySeparatorChar + "T.cs"));
    }

    [Fact]
    public void A_path_whose_pdb_is_missing_stays_unresolved()
        => Assert.Null(DecompiledSources.ToLocalFile("decompiled/NoSuchAssembly/00000000-0000-0000-0000-000000000000/T.cs"));
}
