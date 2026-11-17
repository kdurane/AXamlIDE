using System;
using System.IO;
using AXamlIDE.Services.Terminal;

namespace AXamlIDE.Tests;

public class TerminalTests
{
    [Fact]
    public void A_shell_that_exists_is_found()
    {
        var shell = ShellLocator.Find();

        Assert.False(string.IsNullOrEmpty(shell.Name));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.Exists(shell.Path), shell.Path);
    }
}
