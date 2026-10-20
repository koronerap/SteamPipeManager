using Avalonia;
using SteamPipeManager.Desktop.Widget;

namespace SteamPipeManager.Desktop;

internal static class Program
{
    /// <summary>
    /// Ana uygulama ya da widget. Linux ve macOS'ta widget aynı çalıştırılabilirin
    /// <c>--widget</c> ile açılmışı; Windows'ta ayrı derlenen PipeManagerPulse.exe.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        if (WidgetMode.IsRequested(args))
        {
            BuildWidgetApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>macOS'ta Dock'ta yer tutmuyor; menü çubuğunda yaşıyor.</summary>
    private static AppBuilder BuildWidgetApp() =>
        AppBuilder.Configure<WidgetApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
