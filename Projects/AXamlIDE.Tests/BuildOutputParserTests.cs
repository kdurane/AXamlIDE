using System.IO;
using AXamlIDE.Services.Running;

namespace AXamlIDE.Tests;

public class BuildOutputParserTests
{
    [Fact]
    public void Parses_a_compiler_error_with_its_project()
    {
        var d = BuildOutputParser.TryParseLine(@"C:\src\App\Program.cs(12,9): error CS1002: ; expected [C:\src\App\App.csproj]");
        Assert.NotNull(d);
        Assert.Equal(@"C:\src\App\Program.cs", d.Path);
        Assert.Equal(12, d.Line);
        Assert.Equal(9, d.Column);
        Assert.True(d.IsError);
        Assert.Equal("CS1002", d.Code);
        Assert.Equal("; expected", d.Message);
        Assert.Equal(@"C:\src\App\App.csproj", d.Project);
    }

    [Fact]
    public void Parses_a_warning_without_a_project()
    {
        var d = BuildOutputParser.TryParseLine("/home/me/app/Main.cs(3,5): warning CS0168: The variable 'x' is declared but never used");
        Assert.NotNull(d);
        Assert.Equal(2, d.Severity);
        Assert.Equal("/home/me/app/Main.cs", d.Path);
        Assert.Null(d.Project);
        Assert.Equal("The variable 'x' is declared but never used", d.Message);
    }

    [Fact]
    public void Parses_an_xaml_error_with_only_a_line()
    {
        var d = BuildOutputParser.TryParseLine(@"C:\src\App\Views\MainWindow.axaml(10): error AVLN2000: Unable to resolve type Foo [C:\src\App\App.csproj]");
        Assert.NotNull(d);
        Assert.Equal(10, d.Line);
        Assert.Equal(1, d.Column);
        Assert.Equal("AVLN2000", d.Code);
    }

    [Fact]
    public void Makes_a_relative_path_absolute_from_the_project()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "App");
        var project = Path.Combine(projectDir, "App.csproj");
        var relative = Path.Combine("Views", "MainWindow.axaml");

        var d = BuildOutputParser.TryParseLine($"{relative}(10,2): error AVLN2000: bad [{project}]");

        Assert.NotNull(d);
        Assert.Equal(Path.GetFullPath(Path.Combine(projectDir, relative)), d.Path);
    }

    [Fact]
    public void Parses_an_msbuild_error_with_no_file()
    {
        var d = BuildOutputParser.TryParseLine("MSBUILD : error MSB1009: Project file does not exist.");
        Assert.NotNull(d);
        Assert.Equal("", d.Path);
        Assert.False(d.HasLocation);
        Assert.Equal("MSB1009", d.Code);
        Assert.Equal("Project file does not exist.", d.Message);
    }

    [Fact]
    public void Parses_a_project_level_warning()
    {
        var d = BuildOutputParser.TryParseLine(@"C:\src\App\App.csproj : warning NU1603: Something was resolved [C:\src\App\App.csproj]");
        Assert.NotNull(d);
        Assert.Equal(@"C:\src\App\App.csproj", d.Path);
        Assert.Equal(0, d.Line);
        Assert.Equal("NU1603", d.Code);
    }

    [Theory]
    [InlineData("Build succeeded.")]
    [InlineData("    0 Error(s)")]
    [InlineData("  App -> C:\\src\\App\\bin\\Debug\\net10.0\\App.dll")]
    [InlineData("Time Elapsed 00:00:01.23")]
    [InlineData("")]
    public void Ignores_other_lines(string line) => Assert.Null(BuildOutputParser.TryParseLine(line));

    [Fact]
    public void Parse_returns_each_diagnostic_once()
    {
        string[] output =
        [
            "Determining projects to restore...",
            @"C:\src\App\Program.cs(12,9): error CS1002: ; expected [C:\src\App\App.csproj]",
            "Build FAILED.",
            "",
            @"C:\src\App\Program.cs(12,9): error CS1002: ; expected [C:\src\App\App.csproj]",
            "    0 Warning(s)",
            "    1 Error(s)"
        ];
        Assert.Single(BuildOutputParser.Parse(output));
    }
}
