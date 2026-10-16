using Avalonia;
using System;

namespace AXamlIDE
{
    class Program
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();

        [STAThread]
        public static void Main(string[] args)
        {
            Environment.SetEnvironmentVariable("AXAMLIDE_NETCOREDBG_PATH", "/home/kieron/RiderProjects/AXamlIDE/Projects/netcoredbg/netcoredbg");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }
}