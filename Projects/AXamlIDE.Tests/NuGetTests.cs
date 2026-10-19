using System.IO;
using System.Linq;
using AXamlIDE.Services.NuGet;

namespace AXamlIDE.Tests;

public sealed class NuGetVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.0-alpha", "1.0.0")]                 // a release is newer than its prereleases
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]         // more labels = newer
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]    // numbers sort below text
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]        // numeric labels compare as numbers
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("11.1.0.9782", "11.1.0.9783")]           // a fourth number
    [InlineData("2.9", "10.0")]
    public void Orders_like_NuGet(string older, string newer)
    {
        var a = NuGetVersion.TryParse(older)!;
        var b = NuGetVersion.TryParse(newer)!;
        Assert.True(a < b, $"{older} < {newer}");
        Assert.True(b > a);
    }

    [Fact]
    public void Ignores_build_metadata_and_trailing_zeros()
    {
        Assert.Equal(NuGetVersion.TryParse("1.0.0+build.5"), NuGetVersion.TryParse("1.0.0"));
        Assert.Equal(NuGetVersion.TryParse("1.0"), NuGetVersion.TryParse("1.0.0.0"));
        Assert.Equal("1.0.0+build.5", NuGetVersion.TryParse("1.0.0+build.5")!.Original);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.x")]
    [InlineData("1.0.0-")]
    [InlineData("[1.0,2.0)")]
    public void Rejects_what_isnt_a_version(string text) => Assert.Null(NuGetVersion.TryParse(text));
}

public sealed class NuGetCliTests
{
    [Fact]
    public void Parses_the_configured_sources()
    {
        const string output = """
            Registered Sources:
              1.  nuget.org [Enabled]
                  https://api.nuget.org/v3/index.json
              2.  My Company Feed [Disabled]
                  https://pkgs.example.com/nuget/v3/index.json
              3.  local [Enabled]
                  /home/me/packages
            """;

        var sources = NuGetCli.ParseSources(output);

        Assert.Equal(3, sources.Count);
        Assert.Equal(new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json", true), sources[0]);
        Assert.Equal("My Company Feed", sources[1].Name);
        Assert.False(sources[1].IsEnabled);
        Assert.True(sources[2].IsLocalFolder);
    }

    [Fact]
    public void Parses_installed_packages_across_frameworks_and_skips_implicit_ones()
    {
        const string output = """
            {
              "version": 1,
              "projects": [ { "path": "/p/App.csproj", "frameworks": [
                { "framework": "net8.0", "topLevelPackages": [
                  { "id": "Avalonia", "requestedVersion": "11.0.0", "resolvedVersion": "11.0.0" },
                  { "id": "Microsoft.NETCore.App.Ref", "requestedVersion": "8.0.0", "resolvedVersion": "8.0.0", "autoReferenced": true } ] },
                { "framework": "net10.0", "topLevelPackages": [
                  { "id": "Avalonia", "requestedVersion": "11.0.0", "resolvedVersion": "11.0.0" } ] } ] } ]
            }
            """;

        var installed = NuGetCli.ParseInstalled(output);

        Assert.Null(installed.Problem);
        var avalonia = Assert.Single(installed.Packages);
        Assert.Equal("Avalonia", avalonia.Id);
        Assert.Equal("11.0.0", avalonia.ResolvedVersion);
        Assert.Equal(["net8.0", "net10.0"], avalonia.Frameworks);
    }

    [Fact]
    public void Reports_a_project_that_isnt_restored()
    {
        const string output = """
            { "version": 1, "problems": [ { "level": "error", "text": "No assets file was found for '/p/App.csproj'. Please run restore before running this command." } ] }
            """;

        var installed = NuGetCli.ParseInstalled(output);

        Assert.Empty(installed.Packages);
        Assert.Contains("run restore", installed.Problem);
    }
}

public sealed class NuGetLocalPackagesTests
{
    [Fact]
    public void Reads_a_downloaded_package_from_its_nuspec()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("demo.package/1.2.0/demo.package.nuspec", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Demo.Package</id>
                <version>1.2.0</version>
                <authors>Someone</authors>
                <license type="expression">MIT</license>
                <icon>images\icon.png</icon>
                <projectUrl>https://example.com/demo</projectUrl>
                <description>A demo.</description>
                <tags>ui demo</tags>
                <dependencies>
                  <group targetFramework="net8.0">
                    <dependency id="Other" version="2.0.0" />
                  </group>
                </dependencies>
              </metadata>
            </package>
            """);
        var icon = workspace.Write("demo.package/1.2.0/images/icon.png", "png");

        var details = new NuGetLocalPackages(workspace.Root).TryGetDetails("Demo.Package", "1.2.0");

        Assert.NotNull(details);
        Assert.Equal("Demo.Package", details.Id);
        Assert.Equal("MIT", details.LicenseExpression);
        Assert.Equal(["ui", "demo"], details.Tags);
        Assert.Equal(icon, details.LocalIconPath);
        var group = Assert.Single(details.DependencyGroups);
        Assert.Equal("net8.0", group.TargetFramework);
        Assert.Equal("Other", group.Dependencies.Single().Id);
    }

    [Fact]
    public void Returns_null_for_a_package_that_isnt_downloaded()
    {
        using var workspace = new TempWorkspace();
        Assert.Null(new NuGetLocalPackages(workspace.Root).TryGetDetails("Nope", "1.0.0"));
    }
}
