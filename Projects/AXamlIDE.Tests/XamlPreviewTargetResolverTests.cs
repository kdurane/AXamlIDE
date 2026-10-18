using AXamlIDE.Services.Preview;

namespace AXamlIDE.Tests;

/// <summary>The preview must pick a desktop app project, not a browser/mobile head (Game2048.Browser crash).</summary>
public class XamlPreviewTargetResolverTests
{
    [Theory]
    [InlineData("bin/Debug/net10.0/")]
    [InlineData("bin/Debug/net10.0-windows/")]
    [InlineData("bin\\Release\\net8.0\\")]
    [InlineData(null)]
    public void Desktop_outputs_are_accepted(string? outputPath)
        => Assert.True(XamlPreviewTargetResolver.IsDesktopOutput(outputPath));

    [Theory]
    [InlineData("bin/Debug/net10.0-browser/")]
    [InlineData("bin/Debug/net10.0-android/")]
    [InlineData("bin/Debug/net10.0-ios/")]
    [InlineData("bin\\Debug\\net9.0-maccatalyst\\")]
    public void Browser_and_mobile_outputs_are_rejected(string outputPath)
        => Assert.False(XamlPreviewTargetResolver.IsDesktopOutput(outputPath));
}
