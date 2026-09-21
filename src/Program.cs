using Avalonia;
using System;

namespace OllamaNetGB;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Services.StartupLog.Initialize();
        Console.WriteLine("[startup] Configuring Avalonia");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        Console.WriteLine("[startup] Avalonia lifetime ended");
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
