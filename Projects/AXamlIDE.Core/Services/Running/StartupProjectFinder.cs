using System;

namespace AXamlIDE.Services.Running;

/// <summary>
/// Which projects can be a startup project: the toolbar lists the ones that build an executable
/// (OutputType Exe or WinExe), like Visual Studio's startup project box.
/// </summary>
public static class StartupProjectFinder
{
    public static bool IsRunnable(string? outputType)
        => string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
           || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);
}
