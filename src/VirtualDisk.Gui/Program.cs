using Avalonia;

namespace VirtualDisk.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--version" or "version")
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
