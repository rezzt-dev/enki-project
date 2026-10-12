using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace enki.Services;

/// <summary>Selecciona la pantalla del cursor antes de mostrar la ventana de arranque.</summary>
internal static class StartupWindowPlacement
{
    public static void Apply(Window window)
    {
        var position = TryGetCursorPosition(window);
        if (position is not { } point || window.Screens.ScreenFromPoint(point) is null) return;

        // CenterScreen toma la pantalla de Position antes del primer Show y calcula tanto la escala
        // como el centrado en su área útil. No fijamos Width/Height ni recolocamos tras hacerla visible.
        window.Position = point;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private static PixelPoint? TryGetCursorPosition(Window window)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;

            if (OperatingSystem.IsMacOS())
            {
                var cursorEvent = CGEventCreate(IntPtr.Zero);
                if (cursorEvent == IntPtr.Zero) return null;
                try
                {
                    var location = CGEventGetLocation(cursorEvent);
                    return new PixelPoint((int)location.X, (int)location.Y);
                }
                finally
                {
                    CFRelease(cursorEvent);
                }
            }

            // Solo consultamos X11 si es el backend real de la ventana. Wayland nativo no permite
            // leer la posición global del cursor: en ese caso se conserva la colocación del sistema.
            if (OperatingSystem.IsLinux() && window.TryGetPlatformHandle()?.HandleDescriptor == "XID")
                return TryGetX11CursorPosition();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            LogService.Warning("No se pudo consultar la posición del cursor al abrir Enki", ex);
        }

        return null;
    }

    private static PixelPoint? TryGetX11CursorPosition()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return null;
        try
        {
            return XQueryPointer(display, XDefaultRootWindow(display), out _, out _,
                out var x, out var y, out _, out _, out _) != 0
                ? new PixelPoint(x, y)
                : null;
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacPoint { public double X; public double Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out WindowsPoint point);

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphics)] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CoreGraphics)] private static extern MacPoint CGEventGetLocation(IntPtr cursorEvent);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr displayName);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")]
    private static extern int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root,
        out IntPtr child, out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
}
