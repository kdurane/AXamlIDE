using AXamlIDE.Services.NuGet;
using Xunit;

namespace AXamlIDE.Tests;

public class PackageReferenceEditorTests
{
    // Multi-line attributes: exactly what dotnet add package used to collapse.
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
            <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
            </PropertyGroup>

            <Target Name="Download"
                    Condition="'$(X)' != ''">
                <Message Text="hi" />
            </Target>

            <ItemGroup>
                <PackageReference Include="Avalonia" Version="12.1.4" />
                <PackageReference Include="Markdig" Version="1.4.0">
                    <PrivateAssets>all</PrivateAssets>
                </PackageReference>
            </ItemGroup>
        </Project>
        """;

    [Fact]
    public void Updating_changes_only_the_version()
    {
        var result = PackageReferenceEditor.AddOrUpdate(Project, "avalonia", "12.2.0");
        Assert.Equal(Project.Replace("Version=\"12.1.4\"", "Version=\"12.2.0\""), result);

        var withChildren = PackageReferenceEditor.AddOrUpdate(Project, "Markdig", "1.5.0");
        Assert.Equal(Project.Replace("Version=\"1.4.0\"", "Version=\"1.5.0\""), withChildren);
    }

    [Fact]
    public void Adding_goes_after_the_last_reference_at_its_indentation()
    {
        var result = PackageReferenceEditor.AddOrUpdate(Project, "Dock.Avalonia", "12.1.0.12");
        Assert.Equal(Project.Replace("""
                    </PackageReference>
            """, """
                    </PackageReference>
                    <PackageReference Include="Dock.Avalonia" Version="12.1.0.12" />
            """), result);
    }

    [Fact]
    public void Adding_the_first_reference_makes_an_item_group()
    {
        const string bare = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";
        var result = PackageReferenceEditor.AddOrUpdate(bare, "Markdig", "1.4.0");
        Assert.Equal(
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n\n" +
            "  <ItemGroup>\n    <PackageReference Include=\"Markdig\" Version=\"1.4.0\" />\n  </ItemGroup>\n</Project>\n",
            result);
    }

    [Fact]
    public void Removing_takes_the_whole_element_and_its_line()
    {
        var result = PackageReferenceEditor.Remove(Project, "Markdig");
        Assert.Equal(Project.Replace("""
                    <PackageReference Include="Markdig" Version="1.4.0">
                        <PrivateAssets>all</PrivateAssets>
                    </PackageReference>

            """, ""), result);
    }

    [Fact]
    public void Removing_the_last_reference_removes_the_empty_group()
    {
        var once = PackageReferenceEditor.Remove(Project, "Markdig")!;
        var result = PackageReferenceEditor.Remove(once, "Avalonia");
        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                </PropertyGroup>

                <Target Name="Download"
                        Condition="'$(X)' != ''">
                    <Message Text="hi" />
                </Target>
            </Project>
            """, result);
    }

    [Fact]
    public void Unusual_references_are_left_to_dotnet()
    {
        const string central = "<Project>\n  <ItemGroup>\n    <PackageReference Include=\"Markdig\" />\n  </ItemGroup>\n</Project>";
        Assert.Null(PackageReferenceEditor.AddOrUpdate(central, "Markdig", "1.5.0"));
        Assert.Null(PackageReferenceEditor.AddOrUpdate(central.Replace("Include=\"Markdig\"", "Include=\"Markdig\" Version=\"$(MarkdigVersion)\""), "Markdig", "1.5.0"));
        Assert.Null(PackageReferenceEditor.Remove(Project, "NotThere"));
        Assert.Equal("12.1.4", PackageReferenceEditor.VersionOf(Project, "Avalonia"));
    }

    [Fact]
    public void Windows_line_endings_are_kept()
    {
        var crlf = Project.Replace("\n", "\r\n");
        var result = PackageReferenceEditor.AddOrUpdate(crlf, "Dock.Avalonia", "1.0.0")!;
        Assert.DoesNotContain("\n", result.Replace("\r\n", ""));
    }
}
