using System;
using System.Runtime.InteropServices;
using Avalonia.Input;
using enki.Localization;

namespace enki.Services;

/// <summary>Backend macOS mediante la API estable de hotkeys de Carbon. No usa monitorización global de
/// eventos ni permisos de accesibilidad: el sistema reserva la combinación y entrega solo ese evento.</summary>
internal sealed class MacGlobalHotkey : IGlobalHotkeyBackend
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint EventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint EventHotKeyPressed = 6;
    private const uint KeyDirectObject = 0x2D2D2D2D; // '----'
    private const uint TypeEventHotKeyId = 0x686B6964; // 'hkid'
    private const uint Signature = 0x454E4B49; // 'ENKI'
    private const int EventHotKeyExistsError = -9878;
    private const uint CmdKey = 1u << 8;
    private const uint ShiftKey = 1u << 9;
    private const uint OptionKey = 1u << 11;
    private const uint ControlKey = 1u << 12;

    private readonly EventHandlerDelegate _handlerDelegate;
    private IntPtr _handler;
    private IntPtr _hotkey;
    private Action? _onPressed;
    private bool _disposed;

    public MacGlobalHotkey()
    {
        _handlerDelegate = OnHotkeyEvent;
        var type = new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed };
        var status = InstallApplicationEventHandler(_handlerDelegate, 1, [type], IntPtr.Zero, out _handler);
        if (status != 0) throw new InvalidOperationException($"InstallApplicationEventHandler devolvió {status}.");
    }

    public GlobalHotkeyResult Register(KeyGesture gesture, Action onPressed)
    {
        Unregister();
        if (_disposed) return Failed("QuickCapture_Hotkey_Failed");
        if (!HotkeyKeyMap.TryToMacKeyCode(gesture.Key, out var keyCode))
            return Failed("QuickCapture_Hotkey_UnsupportedKey");

        var modifiers = 0u;
        if ((gesture.KeyModifiers & KeyModifiers.Meta) != 0) modifiers |= CmdKey;
        if ((gesture.KeyModifiers & KeyModifiers.Shift) != 0) modifiers |= ShiftKey;
        if ((gesture.KeyModifiers & KeyModifiers.Alt) != 0) modifiers |= OptionKey;
        if ((gesture.KeyModifiers & KeyModifiers.Control) != 0) modifiers |= ControlKey;

        var id = new EventHotKeyId { Signature = Signature, Id = 1 };
        var status = RegisterEventHotKey(keyCode, modifiers, id, GetApplicationEventTarget(), 0, out _hotkey);
        if (status == 0)
        {
            _onPressed = onPressed;
            return GlobalHotkeyResult.Ok;
        }

        _hotkey = IntPtr.Zero;
        return new GlobalHotkeyResult(status == EventHotKeyExistsError
                ? GlobalHotkeyStatus.Conflict : GlobalHotkeyStatus.Failed,
            LocalizedTextFormatter.Format(status == EventHotKeyExistsError
                ? "QuickCapture_Hotkey_Conflict" : "QuickCapture_Hotkey_Failed"));
    }

    public void Unregister()
    {
        if (_hotkey != IntPtr.Zero) _ = UnregisterEventHotKey(_hotkey);
        _hotkey = IntPtr.Zero;
        _onPressed = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unregister();
        if (_handler != IntPtr.Zero) _ = RemoveEventHandler(_handler);
        _handler = IntPtr.Zero;
        _disposed = true;
    }

    private int OnHotkeyEvent(IntPtr nextHandler, IntPtr eventRef, IntPtr userData)
    {
        var status = GetEventParameter(eventRef, KeyDirectObject, TypeEventHotKeyId, IntPtr.Zero,
            (uint)Marshal.SizeOf<EventHotKeyId>(), IntPtr.Zero, out var id);
        if (status == 0 && id.Signature == Signature && id.Id == 1) _onPressed?.Invoke();
        return status;
    }

    private static GlobalHotkeyResult Failed(string key) =>
        new(GlobalHotkeyStatus.Failed, LocalizedTextFormatter.Format(key));

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec { public uint EventClass; public uint EventKind; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId { public uint Signature; public uint Id; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerDelegate(IntPtr nextHandler, IntPtr eventRef, IntPtr userData);

    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int InstallApplicationEventHandler(EventHandlerDelegate handler, uint count, [In] EventTypeSpec[] types, IntPtr userData, out IntPtr handlerRef);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(IntPtr handlerRef);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId hotkeyId, IntPtr target, uint options, out IntPtr hotkeyRef);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotkeyRef);
    [DllImport(Carbon)] private static extern int GetEventParameter(IntPtr eventRef, uint name, uint desiredType, IntPtr actualType, uint bufferSize, IntPtr actualSize, out EventHotKeyId data);
}
