using System;
using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Threading;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>En qué quedó el intento de reservar el atajo global en el sistema operativo.</summary>
public enum GlobalHotkeyStatus
{
    /// <summary>Reservado: la combinación funciona con Enki en segundo plano.</summary>
    Registered,

    /// <summary>El escritorio no garantiza que el gesto llegue desde otras aplicaciones — Wayland,
    /// donde un cliente no puede capturar teclas globales sin el portal del escritorio.</summary>
    Unreliable,

    /// <summary>Esta plataforma todavía no tiene implementación de atajo global en Enki.</summary>
    Unsupported,

    /// <summary>La combinación ya está cogida por el sistema o por otra aplicación.</summary>
    Conflict,

    /// <summary>Falló por otro motivo (no está la biblioteca, no hay servidor gráfico, tecla que no se
    /// puede expresar en esta plataforma).</summary>
    Failed
}

/// <summary>Resultado de reservar el atajo global, con el motivo ya traducido cuando no salió bien.
/// <see cref="Reason"/> es null solo para <see cref="GlobalHotkeyStatus.Registered"/>.</summary>
public sealed record GlobalHotkeyResult(GlobalHotkeyStatus Status, LocalizedText? Reason)
{
    public static readonly GlobalHotkeyResult Ok = new(GlobalHotkeyStatus.Registered, null);

    public bool IsWorking => Status == GlobalHotkeyStatus.Registered;
}

/// <summary>Una implementación por plataforma de "reservar esta combinación de teclas en todo el sistema".
/// Existe la interfaz —y no una sola clase con <c>#if</c>— porque hay más de una implementación real, el
/// mismo criterio con el que ya conviven <see cref="DpapiPolarCredentialStore"/>,
/// <see cref="KeychainPolarCredentialStore"/> y <see cref="LibsecretPolarCredentialStore"/> detrás de
/// <see cref="IPolarCredentialStore"/>.</summary>
internal interface IGlobalHotkeyBackend : IDisposable
{
    /// <summary>Reserva <paramref name="gesture"/>. <paramref name="onPressed"/> se invoca desde el hilo
    /// propio del backend: quien lo consume es responsable de volver al hilo de interfaz (lo hace
    /// <see cref="GlobalHotkeyService"/>). Idempotente: volver a llamar libera la reserva anterior.</summary>
    GlobalHotkeyResult Register(KeyGesture gesture, Action onPressed);

    void Unregister();
}

/// <summary>
/// El atajo global de la captura rápida (T2, .docs/project-info/captura-rapida-global.md).
///
/// <para><b>Es una mejora progresiva, no un requisito.</b> Avalonia no ofrece atajos globales, así que
/// cada sistema necesita su propia implementación nativa, y en Linux el resultado depende del escritorio:
/// bajo X11 funciona (<c>XGrabKey</c>), bajo Wayland un cliente no puede capturar teclas globales sin
/// pasar por el portal del escritorio. Por eso la captura rápida <em>nunca</em> depende solo de esto: el
/// icono de bandeja, la paleta de comandos y el atajo dentro de la app la ofrecen igual. Que el registro
/// falle es un aviso, no un error de arranque.</para>
///
/// <para>Esta clase es la que ve el resto de la aplicación: elige el backend de la plataforma, traduce el
/// gesto remapeable de <see cref="ShortcutRegistry"/> y devuelve al hilo de interfaz antes de invocar la
/// acción. Cualquier excepción del código nativo se convierte en un <see cref="GlobalHotkeyResult"/> con
/// su motivo: una biblioteca que no está o un servidor gráfico que no responde no pueden tumbar Enki.</para>
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly IGlobalHotkeyBackend? _backend;
    private bool _disposed;

    public GlobalHotkeyService()
    {
        _backend = CreateBackend();
        if (_backend is null)
            LastResult = new GlobalHotkeyResult(GlobalHotkeyStatus.Unsupported,
                LocalizedTextFormatter.Format("QuickCapture_Hotkey_UnsupportedPlatform"));
    }

    /// <summary>Cómo quedó el último <see cref="Apply"/>. Lo consulta el compositor para avisar una sola
    /// vez si el atajo global no se pudo reservar.</summary>
    public GlobalHotkeyResult LastResult { get; private set; } = GlobalHotkeyResult.Ok;

    private static IGlobalHotkeyBackend? CreateBackend()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new WindowsGlobalHotkey();
            if (OperatingSystem.IsLinux()) return new X11GlobalHotkey();
            if (OperatingSystem.IsMacOS()) return new MacGlobalHotkey();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or BadImageFormatException or PlatformNotSupportedException
                                       or InvalidOperationException)
        {
            LogService.Warning("No se pudo preparar el atajo global del sistema", ex);
        }
        return null;
    }

    /// <summary>Reserva <paramref name="gesture"/> y deja <paramref name="onPressed"/> listo para
    /// ejecutarse en el hilo de interfaz. Volver a llamar reemplaza la reserva anterior, que es lo que
    /// hace que remapear el atajo en Ajustes › Atajos tenga efecto sin reiniciar.</summary>
    public GlobalHotkeyResult Apply(KeyGesture gesture, Action onPressed)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ArgumentNullException.ThrowIfNull(onPressed);
        if (_disposed || _backend is null) return LastResult;

        try
        {
            LastResult = _backend.Register(gesture, () => Dispatcher.UIThread.Post(onPressed));
        }
        catch (Exception ex)
        {
            // Red de seguridad sobre código nativo: cualquier cosa que venga de ahí se convierte en un
            // aviso, nunca en una excepción que suba hasta el arranque.
            LogService.Warning("No se pudo registrar el atajo global del sistema", ex);
            LastResult = new GlobalHotkeyResult(GlobalHotkeyStatus.Failed,
                LocalizedTextFormatter.Format("QuickCapture_Hotkey_Failed"));
        }
        return LastResult;
    }

    public void Unregister() => _backend?.Unregister();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend?.Dispose();
    }
}

/// <summary>Traducción del <see cref="Key"/> de Avalonia a lo que entiende cada sistema. Avalonia hereda
/// el enumerado de teclas de WPF, cuyos valores <em>no</em> son ni los códigos virtuales de Windows ni los
/// keysym de X11, así que hace falta una tabla en los dos sentidos.
///
/// <para>Acotada a propósito a las teclas que un atajo global puede llevar razonablemente —letras,
/// dígitos, funciones, y el puñado de teclas con nombre—; cualquier otra devuelve false y el registro
/// falla con un motivo claro en vez de reservar una tecla equivocada.</para></summary>
internal static class HotkeyKeyMap
{
    /// <summary>Códigos virtuales de Windows para lo que no cae en un rango contiguo.</summary>
    private static readonly Dictionary<Key, uint> WindowsVirtualKeys = new()
    {
        [Key.Space] = 0x20, [Key.Enter] = 0x0D, [Key.Tab] = 0x09, [Key.Escape] = 0x1B,
        [Key.Back] = 0x08, [Key.Insert] = 0x2D, [Key.Delete] = 0x2E, [Key.Home] = 0x24,
        [Key.End] = 0x23, [Key.PageUp] = 0x21, [Key.PageDown] = 0x22,
        [Key.Left] = 0x25, [Key.Up] = 0x26, [Key.Right] = 0x27, [Key.Down] = 0x28,
        [Key.OemComma] = 0xBC, [Key.OemPeriod] = 0xBE, [Key.OemPlus] = 0xBB, [Key.OemMinus] = 0xBD,
        [Key.OemQuestion] = 0xBF, [Key.OemSemicolon] = 0xBA, [Key.OemQuotes] = 0xDE,
        [Key.OemOpenBrackets] = 0xDB, [Key.OemCloseBrackets] = 0xDD, [Key.OemBackslash] = 0xE2,
        [Key.OemTilde] = 0xC0, [Key.Add] = 0x6B, [Key.Subtract] = 0x6D
    };

    /// <summary>Nombres de keysym de X11 (los de <c>keysymdef.h</c>) para las mismas teclas.</summary>
    private static readonly Dictionary<Key, string> X11Keysyms = new()
    {
        [Key.Space] = "space", [Key.Enter] = "Return", [Key.Tab] = "Tab", [Key.Escape] = "Escape",
        [Key.Back] = "BackSpace", [Key.Insert] = "Insert", [Key.Delete] = "Delete", [Key.Home] = "Home",
        [Key.End] = "End", [Key.PageUp] = "Prior", [Key.PageDown] = "Next",
        [Key.Left] = "Left", [Key.Up] = "Up", [Key.Right] = "Right", [Key.Down] = "Down",
        [Key.OemComma] = "comma", [Key.OemPeriod] = "period", [Key.OemPlus] = "equal",
        [Key.OemMinus] = "minus", [Key.OemQuestion] = "slash", [Key.OemSemicolon] = "semicolon",
        [Key.OemQuotes] = "apostrophe", [Key.OemOpenBrackets] = "bracketleft",
        [Key.OemCloseBrackets] = "bracketright", [Key.OemBackslash] = "backslash",
        [Key.OemTilde] = "grave", [Key.Add] = "KP_Add", [Key.Subtract] = "KP_Subtract"
    };

    /// <summary>Códigos físicos de teclado de macOS usados por <c>RegisterEventHotKey</c>. El valor no
    /// depende de la distribución activa: es la misma tecla física que Avalonia identifica.</summary>
    private static readonly Dictionary<Key, uint> MacKeyCodes = new()
    {
        [Key.A] = 0, [Key.S] = 1, [Key.D] = 2, [Key.F] = 3, [Key.H] = 4, [Key.G] = 5,
        [Key.Z] = 6, [Key.X] = 7, [Key.C] = 8, [Key.V] = 9, [Key.B] = 11, [Key.Q] = 12,
        [Key.W] = 13, [Key.E] = 14, [Key.R] = 15, [Key.Y] = 16, [Key.T] = 17,
        [Key.D1] = 18, [Key.D2] = 19, [Key.D3] = 20, [Key.D4] = 21, [Key.D6] = 22,
        [Key.D5] = 23, [Key.OemPlus] = 24, [Key.D9] = 25, [Key.D7] = 26,
        [Key.OemMinus] = 27, [Key.D8] = 28, [Key.D0] = 29, [Key.OemCloseBrackets] = 30,
        [Key.O] = 31, [Key.U] = 32, [Key.OemOpenBrackets] = 33, [Key.I] = 34, [Key.P] = 35,
        [Key.Enter] = 36, [Key.L] = 37, [Key.J] = 38, [Key.OemQuotes] = 39, [Key.K] = 40,
        [Key.OemSemicolon] = 41, [Key.OemBackslash] = 42, [Key.OemComma] = 43,
        [Key.OemQuestion] = 44, [Key.N] = 45, [Key.M] = 46, [Key.OemPeriod] = 47,
        [Key.Tab] = 48, [Key.Space] = 49, [Key.OemTilde] = 50, [Key.Back] = 51,
        [Key.Escape] = 53, [Key.F1] = 122, [Key.F2] = 120, [Key.F3] = 99, [Key.F4] = 118,
        [Key.F5] = 96, [Key.F6] = 97, [Key.F7] = 98, [Key.F8] = 100, [Key.F9] = 101,
        [Key.F10] = 109, [Key.F11] = 103, [Key.F12] = 111, [Key.Home] = 115,
        [Key.PageUp] = 116, [Key.Delete] = 117, [Key.End] = 119, [Key.PageDown] = 121,
        [Key.Left] = 123, [Key.Right] = 124, [Key.Down] = 125, [Key.Up] = 126
    };

    public static bool TryToWindowsVirtualKey(Key key, out uint virtualKey)
    {
        if (key >= Key.A && key <= Key.Z) { virtualKey = (uint)('A' + (key - Key.A)); return true; }
        if (key >= Key.D0 && key <= Key.D9) { virtualKey = (uint)('0' + (key - Key.D0)); return true; }
        if (key >= Key.NumPad0 && key <= Key.NumPad9) { virtualKey = (uint)(0x60 + (key - Key.NumPad0)); return true; }
        if (key >= Key.F1 && key <= Key.F24) { virtualKey = (uint)(0x70 + (key - Key.F1)); return true; }

        return WindowsVirtualKeys.TryGetValue(key, out virtualKey);
    }

    public static bool TryToX11KeysymName(Key key, out string keysym)
    {
        if (key >= Key.A && key <= Key.Z) { keysym = ((char)('a' + (key - Key.A))).ToString(); return true; }
        if (key >= Key.D0 && key <= Key.D9) { keysym = ((char)('0' + (key - Key.D0))).ToString(); return true; }
        if (key >= Key.NumPad0 && key <= Key.NumPad9) { keysym = "KP_" + (key - Key.NumPad0); return true; }
        if (key >= Key.F1 && key <= Key.F24) { keysym = "F" + (key - Key.F1 + 1); return true; }

        return X11Keysyms.TryGetValue(key, out keysym!);
    }

    public static bool TryToMacKeyCode(Key key, out uint keyCode) => MacKeyCodes.TryGetValue(key, out keyCode);
}
