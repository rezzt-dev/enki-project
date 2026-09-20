using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace enki.Services;

/// <summary>Reproduce un sonido corto al llegar una notificación (docs/ajustes-docs/04-editor-y-notificaciones.md §2).
/// Cadena de fallback (Fase 5 del roadmap de mejora continua, gap 9/10): asset de audio empaquetado
/// (<c>avares://enki/assets/sounds/notification.wav</c>, reproducido con el mejor mecanismo disponible por
/// plataforma) → comando/sonido nativo del sistema operativo → <see cref="System.Console.Beep()"/> →
/// silencio. Cada eslabón se intenta con un try/catch propio (mismo patrón que <see cref="UrlOpener"/>/
/// <see cref="FolderOpener"/>) y solo se pasa al siguiente si el anterior falla o no está disponible en esa
/// máquina; no hay ninguna librería de audio en el proyecto, así que "reproducir" siempre significa invocar
/// un mecanismo ya presente en el SO (un reproductor de línea de comandos en macOS/Linux, la API winmm en
/// Windows) — es una mejora de ambiente, no una función crítica.</summary>
public static class NotificationSoundPlayer
{
    private const string EmbeddedAssetUri = "avares://enki/assets/sounds/notification.wav";
    private static string? s_extractedAssetPath;

    /// <param name="volumePercent">0–100 (ver <c>AppSettings.NotificationSoundVolume</c>). En 0 no se
    /// intenta reproducir nada, ni siquiera el pitido de <see cref="System.Console.Beep()"/> — es el
    /// "silencio" explícito de la cadena de fallback, no solo un volumen bajo.</param>
    public static void Play(double volumePercent = 100) =>
        TryPlay(volumePercent, TryPlayPackagedAsset, TryPlaySystemSound, TryBeep);

    /// <summary>Núcleo de la cadena de fallback, con cada eslabón inyectable para poder probar el orden y
    /// el corte-en-el-primer-éxito sin depender de reproducir audio real (no hay ningún framework de
    /// mocking en el proyecto — ver <c>enki.Tests</c> para el mismo patrón de "parámetro inyectable solo
    /// para tests" usado en <c>SupabaseRestClient</c>/<c>PolarAuthService</c>).</summary>
    internal static bool TryPlay(double volumePercent, Func<double, bool> playPackagedAsset,
        Func<bool> playSystemSound, Func<bool> beep)
    {
        if (volumePercent <= 0) return false;

        foreach (var attempt in new Func<bool>[]
                 {
                     () => playPackagedAsset(volumePercent),
                     playSystemSound,
                     beep
                 })
        {
            try
            {
                if (attempt()) return true;
            }
            catch
            {
                // Este eslabón no está disponible en esta máquina; pasar al siguiente.
            }
        }

        return false;
    }

    private static bool TryPlayPackagedAsset(double volumePercent)
    {
        var path = EnsureAssetExtracted();
        if (path is null) return false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WinMm.PlaySound(path, WinMm.SND_FILENAME | WinMm.SND_ASYNC);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return TryStartProcess("afplay", ["-v", ToAfplayGain(volumePercent), path]);

        // Linux: paplay soporta --volume en unidades PulseAudio (65536 = 100 %); si no está instalado,
        // aplay reproduce el mismo WAV sin control de volumen propio (mejor un sonido a volumen del
        // sistema que ninguno).
        return TryStartProcess("paplay", [$"--volume={ToPulseVolume(volumePercent)}", path])
            || TryStartProcess("aplay", [path]);
    }

    private static bool TryPlaySystemSound()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WinMm.PlaySound("SystemAsterisk", WinMm.SND_ALIAS | WinMm.SND_ASYNC);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return TryStartProcess("afplay", ["/System/Library/Sounds/Ping.aiff"]);

        return TryStartProcess("paplay", ["/usr/share/sounds/freedesktop/stereo/message.oga"]);
    }

    // Console.Beep(int, int) is annotated [SupportedOSPlatform("windows")] — on Unix it throws
    // PlatformNotSupportedException, which the caller's try/catch already turns into "this link failed,
    // fall through" (here: fall through to silence, since it's the last link). That throw-and-swallow *is*
    // the intended last-resort behavior, not a bug to design around, so the platform-compat warning is
    // suppressed the same way PdfRenderHelper already does for its own known non-issue.
#pragma warning disable CA1416
    private static bool TryBeep()
    {
        Console.Beep(880, 150);
        return true;
    }
#pragma warning restore CA1416

    /// <summary>Extrae el WAV empaquetado a un archivo real, porque los tres mecanismos de reproducción
    /// (winmm, afplay, paplay/aplay) reciben una <b>ruta</b>, no un flujo. Se hace una sola vez por proceso
    /// y se reutiliza en cada aviso posterior.
    ///
    /// <para>Vive en el directorio temporal privado de <see cref="PrivateTempFile"/> y no en la raíz del
    /// temporal del sistema: allí el nombre era fijo y el directorio compartido, así que otra cuenta local
    /// podía dejar <c>enki-notification-sound.wav</c> creado como enlace simbólico a un archivo del usuario
    /// de Enki para que <c>File.Create</c> lo truncara, o sustituir el WAV por otro contenido que la app
    /// reproduciría (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-03). Dentro de un directorio
    /// <c>0700</c> nadie más puede poner ni leer nada, así que el nombre puede seguir siendo fijo — y eso
    /// es lo que evita que cada arranque deje un archivo suelto más: <b>no hay limpieza al salir, y no hace
    /// falta</b>, porque es siempre el mismo archivo, de unos pocos KB, y no contiene nada del usuario.
    /// Borrarlo al cerrar solo obligaría a volver a extraerlo en el arranque siguiente.</para>
    ///
    /// <para>Si ese directorio no es utilizable en esta máquina, <see cref="PrivateTempFile"/> lanza; el
    /// try/catch por eslabón de <see cref="TryPlay"/> lo convierte en "este eslabón no está disponible" y
    /// pasa al sonido del sistema, que es justo para lo que esa cadena de fallback existe.</para></summary>
    private static string? EnsureAssetExtracted()
    {
        if (s_extractedAssetPath is { } cached && File.Exists(cached)) return cached;

        using var stream = AssetLoader.Open(new Uri(EmbeddedAssetUri));
        var path = PrivateTempFile.WriteStable("enki-notification-sound.wav", stream);

        s_extractedAssetPath = path;
        return path;
    }

    private static bool TryStartProcess(string fileName, string[] arguments)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return Process.Start(info) is not null;
    }

    /// <summary>afplay <c>-v</c> es una ganancia lineal donde 1.0 = volumen normal.</summary>
    private static string ToAfplayGain(double volumePercent) =>
        (Math.Clamp(volumePercent, 0, 100) / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>PulseAudio expresa el volumen en unidades donde 65536 (PA_VOLUME_NORM) = 100 %.</summary>
    private static uint ToPulseVolume(double volumePercent) =>
        (uint)(Math.Clamp(volumePercent, 0, 100) / 100.0 * 65536);

    /// <summary>P/Invoke directo a winmm.dll (parte de todas las instalaciones de Windows, sin paquete
    /// NuGet adicional) — <c>System.Media.SoundPlayer</c> no está disponible porque el proyecto apunta a
    /// <c>net8.0</c> puro, no <c>net8.0-windows</c>. Declarar el <c>DllImport</c> aquí no rompe
    /// macOS/Linux: solo se resuelve en tiempo de ejecución si de verdad se invoca, y ambas llamadas están
    /// protegidas por <see cref="RuntimeInformation.IsOSPlatform"/>.</summary>
    private static class WinMm
    {
        internal const uint SND_ASYNC = 0x0001;
        internal const uint SND_FILENAME = 0x00020000;
        internal const uint SND_ALIAS = 0x00010000;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);

        internal static bool PlaySound(string sound, uint flags) => PlaySoundW(sound, IntPtr.Zero, flags);
    }
}
