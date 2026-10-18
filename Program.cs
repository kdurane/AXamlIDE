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
            Environment.SetEnvironmentVariable("AXAMLIDE_DAP_TRACE", "/tmp/dap.log");
            //Environment.SetEnvironmentVariable("AXAMLIDE_NETCOREDBG_PATH", "C:/Users/Kieron/source/repos/kdurane/AXamlIDE/Projects/netcoredbg/netcoredbg.exe");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }
}