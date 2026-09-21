using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Input;
using enki.Localization;

namespace enki.Services;

/// <summary>Backend Win32 basado en <c>RegisterHotKey</c>. Su ventana de mensajes vive en un hilo propio,
/// por lo que no depende de que la ventana principal esté visible ni expone su HWND al resto de la app.</summary>
internal sealed class WindowsGlobalHotkey : IGlobalHotkeyBackend
{
    private const int HotkeyId = 0x454E;
    private const uint WmHotkey = 0x0312;
    private const uint WmClose = 0x0010;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr MessageOnlyWindow = new(-3);
    private static readonly ConcurrentDictionary<IntPtr, WindowsGlobalHotkey> Instances = new();
    private static readonly WndProc WindowProcedure = ProcessWindowMessage;

    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _messageThread;
    private readonly string _windowClass = $"EnkiQuickCaptureHotkey-{Guid.NewGuid():N}";
    private IntPtr _window;
    private Action? _onPressed;
    private Exception? _startupError;
    private bool _disposed;

    public WindowsGlobalHotkey()
    {
        _messageThread = new Thread(MessageLoop) { IsBackground = true, Name = "Enki global hotkey" };
        _messageThread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException("No se pudo iniciar la ventana de mensajes del atajo global.");
        if (_startupError is not null) throw new InvalidOperationException("No se pudo crear la ventana del atajo global.", _startupError);
    }

    public GlobalHotkeyResult Register(KeyGesture gesture, Action onPressed)
    {
        if (_disposed || _window == IntPtr.Zero)
            return Failed("QuickCapture_Hotkey_Failed");
        if (!HotkeyKeyMap.TryToWindowsVirtualKey(gesture.Key, out var key))
            return Failed("QuickCapture_Hotkey_UnsupportedKey");

        UnregisterHotKey(_window, HotkeyId);
        _onPressed = onPressed;
        var modifiers = ModNoRepeat;
        if ((gesture.KeyModifiers & KeyModifiers.Alt) != 0) modifiers |= ModAlt;
        if ((gesture.KeyModifiers & KeyModifiers.Control) != 0) modifiers |= ModControl;
        if ((gesture.KeyModifiers & KeyModifiers.Shift) != 0) modifiers |= ModShift;
        if ((gesture.KeyModifiers & KeyModifiers.Meta) != 0) modifiers |= ModWin;

        if (RegisterHotKey(_window, HotkeyId, modifiers, key)) return GlobalHotkeyResult.Ok;
        var error = Marshal.GetLastWin32Error();
        return new GlobalHotkeyResult(error == 1409 ? GlobalHotkeyStatus.Conflict : GlobalHotkeyStatus.Failed,
            LocalizedTextFormatter.Format(error == 1409
                ? "QuickCapture_Hotkey_Conflict" : "QuickCapture_Hotkey_Failed"));
    }

    public void Unregister()
    {
        if (_window != IntPtr.Zero) UnregisterHotKey(_window, HotkeyId);
        _onPressed = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        if (_window != IntPtr.Zero) PostMessage(_window, WmClose, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _messageThread) _messageThread.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }

    private static GlobalHotkeyResult Failed(string key) =>
        new(GlobalHotkeyStatus.Failed, LocalizedTextFormatter.Format(key));

    private void MessageLoop()
    {
        try
        {
            var windowClass = new WindowClass { WindowProcedure = WindowProcedure, ClassName = _windowClass };
            if (RegisterClass(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _window = CreateWindowEx(0, _windowClass, _windowClass, 0, 0, 0, 0, 0,
                MessageOnlyWindow, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Instances[_window] = this;
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        Instances.TryRemove(_window, out _);
        if (_window != IntPtr.Zero) DestroyWindow(_window);
        UnregisterClass(_windowClass, IntPtr.Zero);
        _window = IntPtr.Zero;
    }

    private static IntPtr ProcessWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId && Instances.TryGetValue(window, out var owner))
        {
            owner._onPressed?.Invoke();
            return IntPtr.Zero;
        }
        if (message == WmClose)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WndProc WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
}
