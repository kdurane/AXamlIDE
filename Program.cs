using Avalonia;
using System;

namespace AXaxmlIDE;

class Program
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    [STAThread]
    public static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("AXAXMLIDE_NETCOREDBG_PATH", "/home/kieron/RiderProjects/AXaxmlIDE/Projects/netcoredbg/netcoredbg");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}