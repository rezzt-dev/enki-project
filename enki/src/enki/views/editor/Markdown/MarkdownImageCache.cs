using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using enki.Services;
using enki.ViewModels;

namespace enki.Views.Editor.Markdown;

internal enum MarkdownImageState
{
    /// <summary>La decodificación está en marcha en segundo plano; el editor dibuja mientras tanto un
    /// hueco del tamaño de un par de líneas.</summary>
    Loading,
    Ready,
    Failed
}

/// <summary>Una imagen tal y como la ve el editor: su bitmap ya decodificado, su tamaño natural, y de qué
/// archivo salió (para poder detectar que ha cambiado en disco).</summary>
internal sealed class MarkdownImageEntry
{
    public MarkdownImageState State { get; set; } = MarkdownImageState.Loading;
    public Bitmap? Bitmap { get; set; }

    /// <summary>Tamaño natural de la imagen en píxeles, leído de la cabecera del archivo — no del bitmap
    /// decodificado, que puede venir reducido. Es lo que decide a qué tamaño se dibuja cuando la nota no
    /// pide uno explícito.</summary>
    public double NaturalWidth { get; set; }
    public double NaturalHeight { get; set; }

    /// <summary>Ruta del archivo del que se cargó, o null si venía de un <c>data:</c> o de la red.</summary>
    public string? SourcePath { get; set; }
    public DateTime SourceWriteUtc { get; set; }
    public DateTime LastFreshnessCheckUtc { get; set; }
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Bytes que ocupa el bitmap decodificado, estimados a 4 por píxel — el presupuesto que
    /// gobierna el desalojo (ver <see cref="MarkdownImageCache"/>).</summary>
    public long ApproximateBytes { get; set; }
}

/// <summary>
/// Caché en memoria de las imágenes que el live preview dibuja dentro de una nota. Existe por una razón
/// muy concreta: el generador de elementos de AvaloniaEdit corre <b>dentro del layout</b> de cada
/// repintado, así que allí no puede decodificarse nada — un PNG de 3 MB decodificado en ese punto
/// congelaría el editor en cada pulsación de tecla. Aquí, en cambio:
///
/// <list type="bullet">
/// <item>una imagen ya vista se devuelve al instante, con una sola búsqueda en un diccionario, que es lo
/// que hace que desplazarse por una nota llena de capturas vaya a la misma velocidad que por una de solo
/// texto;</item>
/// <item>una imagen nueva se decodifica en un hilo de fondo y, al terminar, pide un redibujado — el hueco
/// gris que se ve entretanto dura lo que tarda un decode, no lo que tarda un frame perdido;</item>
/// <item>la decodificación se hace ya reducida al ancho al que la imagen se va a dibujar
/// (<c>Bitmap.DecodeToWidth</c> escala en Skia durante el propio decode), de modo que una foto de 6000 px
/// no ocupa nunca en memoria más de lo que ocupa en pantalla.</item>
/// </list>
///
/// <para>Es estática y compartida por todo el proceso a propósito: la misma imagen puede aparecer en
/// varias notas abiertas a la vez y no tiene sentido decodificarla una vez por pestaña. El precio es un
/// presupuesto de memoria explícito (<see cref="MemoryBudgetBytes"/>), por debajo del cual se van
/// desalojando las entradas menos usadas.</para>
/// </summary>
internal static class MarkdownImageCache
{
    /// <summary>Techo aproximado de memoria de bitmaps decodificados. Al superarlo se desalojan las
    /// entradas menos recientemente usadas hasta volver por debajo.</summary>
    private const long MemoryBudgetBytes = 96L * 1024 * 1024;

    /// <summary>Tamaño máximo que se acepta descargar de una URL remota. Una imagen mayor casi seguro es
    /// un error (un enlace a un ZIP, una página HTML) y no algo que merezca la pena tener en memoria.</summary>
    private const long MaxRemoteBytes = 16L * 1024 * 1024;

    /// <summary>Cada cuánto se comprueba si el archivo de una imagen ya cargada ha cambiado en disco. Una
    /// llamada a <c>File.GetLastWriteTimeUtc</c> es barata, pero hacerla en cada repintado de cada línea
    /// visible no lo sería; con este intervalo, editar una captura en un programa externo se refleja en el
    /// editor casi al momento sin coste apreciable.</summary>
    private static readonly TimeSpan FreshnessInterval = TimeSpan.FromSeconds(2);

    private static readonly Dictionary<string, MarkdownImageEntry> Entries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> InFlight = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>Un único cliente para todas las descargas remotas — crear uno por imagen agotaría los
    /// sockets del sistema, que es el error clásico con <see cref="HttpClient"/>.</summary>
    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    });

    /// <summary>Devuelve la imagen de <paramref name="src"/> si ya está cargada y, si no, arranca su carga
    /// en segundo plano y devuelve una entrada en <see cref="MarkdownImageState.Loading"/>. Nunca bloquea:
    /// se llama desde el layout del editor. <paramref name="onLoaded"/> se invoca en el hilo de UI cuando
    /// la carga termina (bien o mal), para que la vista pida el redibujado.</summary>
    public static MarkdownImageEntry Get(MarkdownImageContext context, string src, int decodeWidth, Action onLoaded)
    {
        var key = MarkdownImageResolver.Key(context, src, decodeWidth);
        var now = DateTime.UtcNow;

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var existing))
            {
                existing.LastUsedUtc = now;
                if (!NeedsReload(existing, now)) return existing;

                // El archivo ha cambiado bajo nuestros pies: se recarga sin quitar de en medio el bitmap
                // anterior, para que la imagen no parpadee a hueco gris mientras llega la nueva versión.
                existing.LastFreshnessCheckUtc = now;
                Start(key, context, src, decodeWidth, onLoaded);
                return existing;
            }

            var entry = new MarkdownImageEntry { LastUsedUtc = now, LastFreshnessCheckUtc = now };
            Entries[key] = entry;
            Start(key, context, src, decodeWidth, onLoaded);
            return entry;
        }
    }

    /// <summary>Olvida las imágenes que no se pudieron cargar, para que el siguiente repintado vuelva a
    /// intentarlo. Lo llama <c>EditorView</c> cuando cambian las preferencias del editor, que es cuando
    /// una imagen antes imposible puede haberse vuelto posible: activar "Imágenes remotas" en Ajustes
    /// tiene que hacer aparecer las imágenes de la nota abierta, no exigir cerrarla y volver a abrirla.
    /// Las que sí cargaron se conservan — recargarlas sería tirar el trabajo ya hecho.</summary>
    public static void ForgetFailed()
    {
        lock (Gate)
        {
            foreach (var key in Entries.Where(e => e.Value.State == MarkdownImageState.Failed)
                         .Select(e => e.Key).ToList())
            {
                Entries.Remove(key);
            }
        }
    }

    /// <summary>Cierto si la entrada ya está resuelta y su archivo de origen se ha modificado en disco
    /// desde que se cargó. Se comprueba como mucho una vez cada <see cref="FreshnessInterval"/>.</summary>
    private static bool NeedsReload(MarkdownImageEntry entry, DateTime now)
    {
        if (entry.State == MarkdownImageState.Loading) return false;
        if (entry.SourcePath is null) return false;
        if (now - entry.LastFreshnessCheckUtc < FreshnessInterval) return false;

        entry.LastFreshnessCheckUtc = now;
        try
        {
            return File.GetLastWriteTimeUtc(entry.SourcePath) != entry.SourceWriteUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            // Esto corre dentro del layout del editor: una ruta que el sistema de archivos rechaza no
            // puede tumbar un repintado, y "no ha cambiado" es la respuesta segura.
            return false;
        }
    }

    /// <summary>Lanza (o deja correr, si ya estaba lanzada) la carga de una entrada. Debe llamarse con
    /// <see cref="Gate"/> tomado.</summary>
    private static void Start(string key, MarkdownImageContext context, string src, int decodeWidth, Action onLoaded)
    {
        if (!InFlight.Add(key)) return;

        _ = Task.Run(() =>
        {
            MarkdownImageEntry loaded;
            try
            {
                loaded = Load(context, src, decodeWidth);
            }
            catch (Exception ex)
            {
                // Una imagen ilegible es un problema del contenido, no de la app: se anota y se dibuja el
                // hueco de "imagen rota", sin tumbar el repintado del editor.
                LogService.Warning($"No se pudo cargar la imagen '{src}' de una nota", ex);
                loaded = new MarkdownImageEntry { State = MarkdownImageState.Failed };
            }

            lock (Gate)
            {
                InFlight.Remove(key);
                if (Entries.TryGetValue(key, out var target)) Apply(target, loaded);
                else Entries[key] = loaded;
                Trim();
            }

            Dispatcher.UIThread.Post(onLoaded, DispatcherPriority.Render);
        });
    }

    private static void Apply(MarkdownImageEntry target, MarkdownImageEntry loaded)
    {
        target.State = loaded.State;
        target.Bitmap = loaded.Bitmap ?? target.Bitmap;
        target.NaturalWidth = loaded.NaturalWidth;
        target.NaturalHeight = loaded.NaturalHeight;
        target.SourcePath = loaded.SourcePath;
        target.SourceWriteUtc = loaded.SourceWriteUtc;
        target.ApproximateBytes = loaded.ApproximateBytes;
        target.LastFreshnessCheckUtc = DateTime.UtcNow;
    }

    private static MarkdownImageEntry Load(MarkdownImageContext context, string src, int decodeWidth)
    {
        var target = MarkdownImageResolver.Resolve(context, src);
        switch (target.Kind)
        {
            case MarkdownImageKind.File:
            {
                if (context.ReadBytes is not { } readBytes || !File.Exists(target.Value))
                    return new MarkdownImageEntry { State = MarkdownImageState.Failed, SourcePath = target.Value };

                var writeUtc = File.GetLastWriteTimeUtc(target.Value);
                var entry = Decode(readBytes(target.Value), decodeWidth, Path.GetExtension(target.Value));
                entry.SourcePath = target.Value;
                entry.SourceWriteUtc = writeUtc;
                return entry;
            }
            case MarkdownImageKind.DataUri:
            {
                var bytes = MarkdownImageResolver.DecodeDataUri(target.Value);
                return bytes is null
                    ? new MarkdownImageEntry { State = MarkdownImageState.Failed }
                    : Decode(bytes, decodeWidth, MediaTypeExtension(target.Value));
            }
            case MarkdownImageKind.Remote:
            {
                if (!EditorPreferences.RemoteImages) return new MarkdownImageEntry { State = MarkdownImageState.Failed };
                return Decode(Download(target.Value), decodeWidth, Path.GetExtension(target.Value));
            }
            default:
                return new MarkdownImageEntry { State = MarkdownImageState.Failed };
        }
    }

    private static byte[] Download(string url)
    {
        using var response = Http.Value.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxRemoteBytes)
            throw new InvalidOperationException($"La imagen remota supera el máximo de {MaxRemoteBytes} bytes");

        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (bytes.LongLength > MaxRemoteBytes)
            throw new InvalidOperationException($"La imagen remota supera el máximo de {MaxRemoteBytes} bytes");

        return bytes;
    }

    /// <summary>Decodifica los bytes de una imagen al ancho pedido. El SVG pasa por
    /// <see cref="SvgRenderHelper"/> (Avalonia no lo decodifica de forma nativa), y el resto por Skia:
    /// primero se lee la cabecera para conocer el tamaño natural y no ampliar nunca una imagen pequeña —
    /// <c>DecodeToWidth</c> escala en ambos sentidos, y estirar un icono de 32 px hasta el ancho de la
    /// columna lo dejaría borroso y ocupando memoria de más.</summary>
    private static MarkdownImageEntry Decode(byte[] data, int decodeWidth, string extension)
    {
        if (data.Length == 0) return new MarkdownImageEntry { State = MarkdownImageState.Failed };

        if (string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase))
        {
            var vector = SvgRenderHelper.RenderSvg(data, decodeWidth, allowUpscale: false);
            return Ready(vector, vector.PixelSize.Width, vector.PixelSize.Height);
        }

        // El tamaño se lee sobre su propio flujo: SKCodec toma posesión del que se le pasa y lo cierra al
        // liberarse, así que reutilizar aquí el flujo de la decodificación lo dejaba cerrado justo antes
        // de decodificar — y toda imagen acababa marcada como rota.
        var natural = ReadNaturalSize(data);

        using var stream = new MemoryStream(data, writable: false);
        var bitmap = natural.Width > 0 && natural.Width <= decodeWidth
            ? new Bitmap(stream)
            : Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);

        var width = natural.Width > 0 ? natural.Width : bitmap.PixelSize.Width;
        var height = natural.Height > 0 ? natural.Height : bitmap.PixelSize.Height;
        return Ready(bitmap, width, height);
    }

    private static MarkdownImageEntry Ready(Bitmap bitmap, double naturalWidth, double naturalHeight) => new()
    {
        State = MarkdownImageState.Ready,
        Bitmap = bitmap,
        NaturalWidth = naturalWidth,
        NaturalHeight = naturalHeight,
        ApproximateBytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4
    };

    /// <summary>Tamaño en píxeles leído solo de la cabecera del archivo, sin decodificar sus píxeles.
    /// Devuelve (0, 0) si Skia no reconoce el formato, en cuyo caso el decodificador de Avalonia tendrá la
    /// última palabra.</summary>
    private static (int Width, int Height) ReadNaturalSize(byte[] data)
    {
        try
        {
            using var probe = new MemoryStream(data, writable: false);
            using var codec = SkiaSharp.SKCodec.Create(probe);
            return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return (0, 0);
        }
    }

    private static string MediaTypeExtension(string dataUri)
    {
        // "data:image/png;base64,…" → ".png". Sirve solo para distinguir el SVG del resto.
        var start = "data:image/".Length;
        var end = dataUri.IndexOfAny([';', ','], start);
        if (end <= start) return string.Empty;

        var subtype = dataUri[start..end].ToLowerInvariant();
        return subtype == "svg+xml" ? ".svg" : "." + subtype;
    }

    /// <summary>Desaloja entradas hasta volver por debajo del presupuesto, empezando por la menos usada.
    /// Los bitmaps desalojados no se liberan a mano: una línea visual ya dibujada puede seguir
    /// enseñándolos, y destruirlos bajo ella rompería el repintado — se dejan al recolector, que es quien
    /// sabe cuándo nadie los mira ya. Debe llamarse con <see cref="Gate"/> tomado.</summary>
    private static void Trim()
    {
        long total = 0;
        foreach (var entry in Entries.Values) total += entry.ApproximateBytes;
        if (total <= MemoryBudgetBytes) return;

        var ordered = new List<KeyValuePair<string, MarkdownImageEntry>>(Entries);
        ordered.Sort((a, b) => a.Value.LastUsedUtc.CompareTo(b.Value.LastUsedUtc));

        foreach (var (key, entry) in ordered)
        {
            if (total <= MemoryBudgetBytes) break;
            if (entry.State == MarkdownImageState.Loading) continue;
            Entries.Remove(key);
            total -= entry.ApproximateBytes;
        }
    }
}
