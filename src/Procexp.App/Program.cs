using Avalonia;

namespace Procexp.App;

internal static class Program
{
    // Avalonia configuration. Must not touch any Avalonia type before
    // AppMain is called — the SynchronizationContext is not installed yet.
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
