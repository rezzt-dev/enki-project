using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Input;
using enki.Localization;

namespace enki.Services;

/// <summary>Backend X11 basado en <c>XGrabKey</c>. En Wayland se declara no fiable y no intenta fingir una
/// captura global mediante XWayland; la bandeja y la acción local siguen disponibles.</summary>
internal sealed class X11GlobalHotkey : IGlobalHotkeyBackend
{
    private const int KeyPress = 2;
    private const long KeyPressMask = 1L;
    private const int GrabModeAsync = 1;
    private const uint ShiftMask = 1;
    private const uint ControlMask = 4;
    private const uint Mod1Mask = 8;
    private const uint Mod2Mask = 16;
    private const uint Mod4Mask = 64;
    private const uint LockMask = 2;
    private const byte BadAccess = 10;
    private static readonly object ErrorHandlerGate = new();
    private static readonly XErrorHandler ErrorHandler = CaptureXError;
    private static byte _lastXError;

    private CancellationTokenSource? _cancellation;
    private Thread? _eventThread;
    private bool _disposed;

    public GlobalHotkeyResult Register(KeyGesture gesture, Action onPressed)
    {
        Unregister();
        if (_disposed) return Failed("QuickCapture_Hotkey_Failed");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return new GlobalHotkeyResult(GlobalHotkeyStatus.Unreliable,
                LocalizedTextFormatter.Format("QuickCapture_Hotkey_Wayland"));
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return new GlobalHotkeyResult(GlobalHotkeyStatus.Unsupported,
                LocalizedTextFormatter.Format("QuickCapture_Hotkey_NoDisplay"));
        if (!HotkeyKeyMap.TryToX11KeysymName(gesture.Key, out var keysymName))
            return Failed("QuickCapture_Hotkey_UnsupportedKey");

        var ready = new ManualResetEventSlim();
        GlobalHotkeyResult? result = null;
        _cancellation = new CancellationTokenSource();
        _eventThread = new Thread(() => EventLoop(keysymName, gesture.KeyModifiers, onPressed,
            ready, value => result = value, _cancellation.Token))
        { IsBackground = true, Name = "Enki X11 global hotkey" };
        _eventThread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(3)))
        {
            Unregister();
            return Failed("QuickCapture_Hotkey_Failed");
        }
        ready.Dispose();
        return result ?? Failed("QuickCapture_Hotkey_Failed");
    }

    public void Unregister()
    {
        _cancellation?.Cancel();
        if (_eventThread is not null && Thread.CurrentThread != _eventThread)
            _eventThread.Join(TimeSpan.FromSeconds(1));
        _cancellation?.Dispose();
        _cancellation = null;
        _eventThread = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unregister();
        _disposed = true;
    }

    private static void EventLoop(string keysymName, KeyModifiers modifiers, Action onPressed,
        ManualResetEventSlim ready, Action<GlobalHotkeyResult> report, CancellationToken cancellation)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            report(Failed("QuickCapture_Hotkey_NoDisplay"));
            ready.Set();
            return;
        }

        var root = XDefaultRootWindow(display);
        var keysym = XStringToKeysym(keysymName);
        var keycode = XKeysymToKeycode(display, keysym);
        if (keysym == IntPtr.Zero || keycode == 0)
        {
            _ = XCloseDisplay(display);
            report(Failed("QuickCapture_Hotkey_UnsupportedKey"));
            ready.Set();
            return;
        }

        var mask = ToModifierMask(modifiers);
        var lockVariants = new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask };
        lock (ErrorHandlerGate)
        {
            _lastXError = 0;
            var previousHandler = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(ErrorHandler));
            foreach (var locks in lockVariants)
                _ = XGrabKey(display, keycode, mask | locks, root, true, GrabModeAsync, GrabModeAsync);
            _ = XSelectInput(display, root, KeyPressMask);
            _ = XSync(display, false);
            _ = XSetErrorHandler(previousHandler);
        }
        if (_lastXError != 0)
        {
            foreach (var locks in lockVariants) _ = XUngrabKey(display, keycode, mask | locks, root);
            _ = XCloseDisplay(display);
            report(new GlobalHotkeyResult(_lastXError == BadAccess
                    ? GlobalHotkeyStatus.Conflict : GlobalHotkeyStatus.Failed,
                LocalizedTextFormatter.Format(_lastXError == BadAccess
                    ? "QuickCapture_Hotkey_Conflict" : "QuickCapture_Hotkey_Failed")));
            ready.Set();
            return;
        }
        report(GlobalHotkeyResult.Ok);
        ready.Set();

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                while (XPending(display) > 0)
                {
                    _ = XNextEvent(display, out var xevent);
                    if (xevent.Type == KeyPress && xevent.Key.KeyCode == keycode &&
                        (xevent.Key.State & ~(LockMask | Mod2Mask)) == mask)
                        onPressed();
                }
                cancellation.WaitHandle.WaitOne(30);
            }
        }
        finally
        {
            foreach (var locks in lockVariants) _ = XUngrabKey(display, keycode, mask | locks, root);
            _ = XSync(display, false);
            _ = XCloseDisplay(display);
        }
    }

    private static uint ToModifierMask(KeyModifiers modifiers)
    {
        var mask = 0u;
        if ((modifiers & KeyModifiers.Shift) != 0) mask |= ShiftMask;
        if ((modifiers & KeyModifiers.Control) != 0) mask |= ControlMask;
        if ((modifiers & KeyModifiers.Alt) != 0) mask |= Mod1Mask;
        if ((modifiers & KeyModifiers.Meta) != 0) mask |= Mod4Mask;
        return mask;
    }

    private static GlobalHotkeyResult Failed(string key) =>
        new(GlobalHotkeyStatus.Failed, LocalizedTextFormatter.Format(key));

    private static int CaptureXError(IntPtr display, ref XErrorEvent error)
    {
        _lastXError = error.ErrorCode;
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XKeyEvent
    {
        public int Type;
        public IntPtr Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
        public IntPtr Root;
        public IntPtr Subwindow;
        public IntPtr Time;
        public int X;
        public int Y;
        public int XRoot;
        public int YRoot;
        public uint State;
        public uint KeyCode;
        public int SameScreen;
    }

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(0)] public XKeyEvent Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XErrorEvent
    {
        public int Type;
        public IntPtr Display;
        public IntPtr ResourceId;
        public IntPtr Serial;
        public byte ErrorCode;
        public byte RequestCode;
        public byte MinorCode;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(IntPtr display, ref XErrorEvent error);

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr displayName);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6", ExactSpelling = true, CharSet = CharSet.Ansi,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr XStringToKeysym([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport("libX11.so.6")] private static extern int XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport("libX11.so.6")] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window, bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport("libX11.so.6")] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window);
    [DllImport("libX11.so.6")] private static extern int XSelectInput(IntPtr display, IntPtr window, long eventMask);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, bool discard);
    [DllImport("libX11.so.6")] private static extern int XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(IntPtr display, out XEvent xevent);
    [DllImport("libX11.so.6")] private static extern IntPtr XSetErrorHandler(IntPtr handler);
}
