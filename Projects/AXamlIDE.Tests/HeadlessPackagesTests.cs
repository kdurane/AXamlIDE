using AXamlIDE.Services.Preview;

namespace AXamlIDE.Tests;

public class HeadlessPackagesTests
{
    [Fact]
    public void The_avalonia_version_comes_from_the_deps_libraries()
    {
        const string deps = """
            {
              "libraries": {
                "Contoso/1.0.0": { "type": "project" },
                "Avalonia.Desktop/11.3.12": { "type": "package" },
                "Avalonia/11.3.12": { "type": "package" }
              }
            }
            """;

        Assert.Equal("11.3.12", HeadlessPackages.AvaloniaVersion(deps));
    }

    [Fact]
    public void No_avalonia_gives_null()
        => Assert.Null(HeadlessPackages.AvaloniaVersion("""{ "libraries": { "Contoso/1.0.0": {} } }"""));
}
