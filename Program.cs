using System;
using Avalonia;

namespace AXamlIDE;

class Program
{
    // Environment variables the app reads (all optional):
    //   AXAMLIDE_NETCOREDBG_PATH  debugger executable (default: bundled netcoredbg/, then PATH)
    //   AXAMLIDE_DAP_TRACE        file to log debug-adapter traffic to (set in Properties/launchSettings.json)
    //   AXAMLIDE_HOST             MSBuild host executable (default: bundled msbuildhost/)
    //   AXAMLIDE_LSP / AXAMLIDE_ROSLYN / AXAMLIDE_CSHARP_LS  language server executables
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}
