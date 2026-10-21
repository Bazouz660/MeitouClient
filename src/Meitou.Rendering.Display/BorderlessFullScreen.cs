using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Meitou.Rendering.Display;

/// <summary>
/// Borderless full screen for the viewer and the game (Alt+Enter, the Tab panel, the viewer's F2 editor button): the window loses its border and covers its monitor, so
/// nothing of the desktop shows and switching away is instant (no exclusive mode change). Toggling again gives the bordered, maximized window back.
/// Off Windows the window manager is asked for full screen instead (<see cref="WindowState.Fullscreen"/>): KDE and other desktops keep their panel
/// above a window that merely covers the monitor, and Wayland compositors ignore the position a client asks for.
/// </summary>
public static class BorderlessFullScreen
{
    public static bool IsOn(IWindow window) => window.WindowBorder == WindowBorder.Hidden || window.WindowState == WindowState.Fullscreen;

    public static void Toggle(IWindow window)
    {
        if (IsOn(window))
        {
            if (window.WindowState == WindowState.Fullscreen) window.WindowState = WindowState.Normal;
            window.WindowBorder = WindowBorder.Resizable;
            window.WindowState = WindowState.Maximized;
            Console.WriteLine("window    bordered");
            return;
        }
        // The monitor the window is on: its origin in desktop coordinates, which is what Position takes. The size is the monitor's video
        // mode: GLFW's Bounds is the work area (1920 x 1032 over a 48 px taskbar, Observed 2026-10-10), and Windows keeps the taskbar on
        // top of a window that does not cover the whole monitor.
        var monitor = window.Monitor ?? Silk.NET.Windowing.Monitor.GetMainMonitor(window);
        var size = monitor.VideoMode.Resolution ?? monitor.Bounds.Size;
        if (!OperatingSystem.IsWindows())
        {
            window.WindowState = WindowState.Fullscreen;
            Console.WriteLine($"window    full screen {size.X} x {size.Y}, asked of the window manager");
            return;
        }
        // Bounds is the work area, so its origin is below a taskbar or panel at the top (or right of one on the left): the corner of the monitor itself is
        // read from GLFW (under Wine too, where a panel at the top left the window 34 px down with its bottom cut off).
        var origin = MonitorCorner(window) ?? monitor.Bounds.Origin;
        // Only when it is not: setting it restores the window, which shows one still hidden (the game sizes its window before showing it).
        if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
        window.WindowBorder = WindowBorder.Hidden;
        window.Position = origin;
        window.Size = size;
        Console.WriteLine($"window    borderless full screen {size.X} x {size.Y} at {origin.X},{origin.Y} (monitor work area {monitor.Bounds.Size.X} x {monitor.Bounds.Size.Y} at {monitor.Bounds.Origin.X},{monitor.Bounds.Origin.Y})");
    }

    /// <summary>The top-left corner of the monitor the window is on (the one under its centre, else the primary), or null when GLFW cannot say.</summary>
    static unsafe Vector2D<int>? MonitorCorner(IWindow window)
    {
        try
        {
            var glfw = Glfw.GetApi();
            var monitors = glfw.GetMonitors(out int count);
            var centre = window.Position + window.Size / 2;
            Silk.NET.GLFW.Monitor* found = null;
            for (int i = 0; i < count; i++)
            {
                glfw.GetMonitorWorkarea(monitors[i], out int x, out int y, out int w, out int h);
                if (centre.X >= x && centre.X < x + w && centre.Y >= y && centre.Y < y + h) { found = monitors[i]; break; }
            }
            if (found == null) found = glfw.GetPrimaryMonitor();
            if (found == null) return null;
            glfw.GetMonitorPos(found, out int px, out int py);
            return new Vector2D<int>(px, py);
        }
        catch (Exception e) when (e is NotSupportedException or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
