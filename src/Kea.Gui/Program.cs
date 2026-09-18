using Avalonia;

namespace Kea.Gui;

internal static class Program
{
    /// <summary>
    /// Entry point. The original carried <c>[STAThread]</c> and WinForms' visual-style calls;
    /// Avalonia sets up its own windowing on each platform instead.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Referenced by name by the Avalonia XAML previewer, so keep the signature.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()   // Win32, macOS Cocoa or X11/Wayland, chosen at runtime
            .WithInterFont()
            .LogToTrace();
}
