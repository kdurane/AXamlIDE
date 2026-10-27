using AXamlIDE.Services.Running;
using Xunit;

namespace AXamlIDE.Tests;

public sealed class StartupProjectFinderTests
{
    [Theory]
    [InlineData("Exe", true)]
    [InlineData("winexe", true)]
    [InlineData("Library", false)]
    [InlineData(null, false)]
    public void IsRunnable_OnlyForExecutables(string? outputType, bool expected)
        => Assert.Equal(expected, StartupProjectFinder.IsRunnable(outputType));
}
