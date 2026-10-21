using Avalonia;

namespace Meitou.Launcher;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // First: Velopack runs its install, update and uninstall hooks here and may exit (docs/launcher.md, "Self-update").
        Velopack.VelopackApp.Build().Run();
        NativeLibraries.Load();
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
