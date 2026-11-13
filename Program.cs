using System;
using System.Threading.Tasks;
using Avalonia;
using AXamlIDE.Services.Diagnostics;

namespace AXamlIDE;

class Program
{
    // Environment variables the app reads (all optional):
    //   AXAMLIDE_NETCOREDBG_PATH  debugger executable (default: bundled netcoredbg/, then PATH)
    //   AXAMLIDE_DAP_TRACE        file to log debug-adapter traffic to (set in Properties/launchSettings.json)
    //   AXAMLIDE_HOST             MSBuild host executable (default: bundled msbuildhost/)
    //   AXAMLIDE_LSP / AXAMLIDE_ROSLYN / AXAMLIDE_CSHARP_LS  language server executables
    //
    // Unhandled exceptions go to CrashLog (%AppData%/AXamlIDE/logs/crashes.log). Ones on the UI thread are also
    // caught in App.axaml.cs, where the IDE reports them in the status bar and carries on.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                CrashLog.Write(e.IsTerminating ? "fatal" : "unhandled", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("task", e.Exception);
            e.SetObserved();   // a background task's failure is logged, not fatal
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            CrashLog.Write("fatal", exception);
            throw;
        }
    }
}
