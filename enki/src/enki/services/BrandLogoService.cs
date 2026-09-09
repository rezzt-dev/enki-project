using System;
using System.IO;
using SkiaSharp;

namespace enki.Services;

/// <summary>Por qué falló <see cref="BrandLogoService.Import"/>, para que el viewmodel elija el aviso
/// traducido sin que el servicio dependa de los recursos de localización (mismo reparto que
/// <c>CustomThemeService.Import</c>).</summary>
public enum BrandLogoImportFailure
{
    None,
    /// <summary>El archivo elegido ya no está donde el diálogo dijo (movido/borrado entre medias).</summary>
    FileNotFound,
    /// <summary>Más grande que <see cref="BrandLogoService.MaxSourceBytes"/>: decodificarlo entero en
    /// memoria para acabar guardando 256 px no compensa.</summary>
    TooLarge,
    /// <summary>Skia no supo decodificarlo: no es una imagen, o es un formato que no entiende (SVG, por
    /// ejemplo, que sí acepta el editor de notas pero aquí no aporta — el logo se rasteriza igual).</summary>
    UnsupportedFormat,
    /// <summary>La imagen se leyó y optimizó bien, pero no se pudo escribir en <c>%AppData%/Enki</c>.</summary>
    WriteFailed
}

/// <summary>Resultado de importar un logo: además de si salió bien, cuánto ocupaba el original y cuánto
/// ocupa ya optimizado, que es lo que Ajustes enseña debajo del logo ("1,4 MB → 18 KB").</summary>
public sealed record BrandLogoImportResult(
    bool Success,
    BrandLogoImportFailure Failure = BrandLogoImportFailure.None,
    long OriginalBytes = 0,
    long StoredBytes = 0);

/// <summary>
/// Ajustes › Apariencia › Logo: guarda la imagen que el usuario elige para el brand header del sidebar
/// (<c>MainWindow.axaml</c>, esquina superior izquierda) y la deja lista para pintarse.
///
/// <para><b>Optimización.</b> El logo se dibuja en un cuadro de 32×32 px, así que guardar el archivo
/// original —un PNG exportado a 2000 px, la foto de un móvil— sería almacenar dos órdenes de magnitud más
/// de píxeles de los que se llegan a ver nunca. Al importar se reescala a <see cref="MaxDimensionPixels"/>
/// px de lado mayor (holgura de sobra para pantallas HiDPI y para el zoom de interfaz al 200%: 32 × 2 × 2 =
/// 128) y se recodifica. Nada de esto es una pérdida de calidad visible al tamaño en el que se muestra, y
/// el archivo pasa de megabytes a decenas de kilobytes.</para>
///
/// <para><b>Formato.</b> Se codifica en PNG y, si la imagen no tiene ni un píxel transparente, también en
/// JPEG de calidad <see cref="JpegQuality"/>; el JPEG solo se conserva si ahorra de verdad
/// (<see cref="JpegMustBeatPngBy"/>). Una foto pierde con PNG por goleada y el JPEG gana claro; un logo
/// plano de pocos colores se queda en PNG, que además es sin pérdida. El margen existe porque a 256 px los
/// dos candidatos de un logo plano rondan el kilobyte, y cambiar lo sin pérdida por lo que tiene pérdida
/// para ahorrar cincuenta bytes sería vender calidad por nada. Una imagen con transparencia ni siquiera
/// se codifica en JPEG, que no la admite y la rellenaría de negro.</para>
///
/// <para>La imagen vive fuera de <c>settings.json</c>, en un archivo propio dentro de la misma carpeta
/// (<see cref="AppDataDirectory"/>) — meterla como base64 en los ajustes engordaría un JSON que se lee y
/// reescribe en cada cambio de cualquier preferencia, y rompería "Exportar ajustes", que es un archivo de
/// texto pensado para moverse entre máquinas. Que esté aparte también es lo que permite que apagar
/// <see cref="Models.AppSettings.UseCustomBrandLogo"/> vuelva al icono por defecto sin perder la imagen
/// importada.</para>
/// </summary>
public sealed class BrandLogoService
{
    /// <summary>Lado mayor al que se reescala la imagen importada. Ver la nota de optimización arriba.</summary>
    public const int MaxDimensionPixels = 256;

    /// <summary>Calidad JPEG del candidato con pérdida. 92 está por encima del umbral en el que aparecen
    /// artefactos visibles, y a 256 px de lado el archivo sigue siendo de pocos kilobytes.</summary>
    private const int JpegQuality = 92;

    /// <summary>Cuánto tiene que bajar el JPEG respecto al PNG para que compense cambiar a un formato con
    /// pérdida: un 20 %. Ver la nota de formato en la clase.</summary>
    private const double JpegMustBeatPngBy = 0.8;

    /// <summary>Techo del archivo de origen. No es un límite del formato sino de memoria: decodificar un
    /// TIFF de 200 MB para quedarnos con 256 px sería pagar un pico de RAM absurdo por el mismo
    /// resultado.</summary>
    public const long MaxSourceBytes = 64L * 1024 * 1024;

    /// <summary>Extensiones que ofrece el diálogo de "elegir imagen". Es un subconjunto de las que acepta
    /// el editor de notas (<see cref="NoteImageAttachments"/>): fuera queda SVG, que Skia no decodifica
    /// como mapa de bits.</summary>
    public static readonly string[] PickerPatterns =
        ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif", "*.ico"];

    private readonly string _pngPath;
    private readonly string _jpegPath;

    /// <summary>Se dispara tras importar o quitar el logo, para que el brand header del sidebar lo
    /// recargue en caliente. Mismo patrón de evento que el resto de servicios (<c>KanbanService.BoardChanged</c>,
    /// <c>SettingsService.SettingsChanged</c>): no basta con <c>SettingsChanged</c> porque reemplazar la
    /// imagen no cambia ningún ajuste, solo el archivo.</summary>
    public event Action? LogoChanged;

    /// <param name="storageDirectory">Sustituye la carpeta <c>%AppData%/Enki</c> por defecto — sin uso en
    /// la app real, pero deja que los tests escriban en un directorio desechable.</param>
    public BrandLogoService(string? storageDirectory = null)
    {
        var directory = storageDirectory ?? AppDataDirectory.Resolve();
        Directory.CreateDirectory(directory);
        _pngPath = Path.Combine(directory, "brand-logo.png");
        _jpegPath = Path.Combine(directory, "brand-logo.jpg");
    }

    /// <summary>Ruta del logo importado, o <c>null</c> si el usuario no ha elegido ninguno todavía. Solo
    /// puede existir uno de los dos archivos a la vez: <see cref="Import"/> borra el del formato que
    /// descarta.</summary>
    public string? LogoPath =>
        File.Exists(_pngPath) ? _pngPath :
        File.Exists(_jpegPath) ? _jpegPath : null;

    public bool HasLogo => LogoPath is not null;

    /// <summary>Lee el logo guardado como bytes en vez de dejar que quien lo pinta abra el archivo: así
    /// nadie mantiene un <see cref="FileStream"/> vivo sobre él y reemplazarlo desde Ajustes no choca con
    /// el sidebar que lo está mostrando. Devuelve <c>null</c> si no hay logo o si el archivo dejó de ser
    /// legible (borrado a mano, disco desconectado), caso en el que la vista cae al icono por defecto.</summary>
    public byte[]? ReadLogoBytes()
    {
        try
        {
            return LogoPath is { } path ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudo leer el logo personalizado", ex);
            return null;
        }
    }

    /// <summary>Optimiza <paramref name="sourcePath"/> y lo guarda como el logo activo, reemplazando al
    /// anterior si lo había. Un fallo deja el logo previo intacto: la escritura solo ocurre cuando la
    /// imagen ya se decodificó y recodificó con éxito.</summary>
    public BrandLogoImportResult Import(string sourcePath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(sourcePath);
            if (!info.Exists) return new BrandLogoImportResult(false, BrandLogoImportFailure.FileNotFound);
            if (info.Length > MaxSourceBytes) return new BrandLogoImportResult(false, BrandLogoImportFailure.TooLarge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogService.Warning("No se pudo leer la imagen elegida para el logo", ex);
            return new BrandLogoImportResult(false, BrandLogoImportFailure.FileNotFound);
        }

        byte[] encoded;
        bool isPng;
        try
        {
            using var decoded = SKBitmap.Decode(sourcePath);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
                return new BrandLogoImportResult(false, BrandLogoImportFailure.UnsupportedFormat);

            using var scaled = ScaleToFit(decoded);
            (encoded, isPng) = EncodeSmallest(scaled ?? decoded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            LogService.Warning("No se pudo decodificar la imagen elegida para el logo", ex);
            return new BrandLogoImportResult(false, BrandLogoImportFailure.UnsupportedFormat);
        }

        try
        {
            // Primero el archivo nuevo y después el del otro formato: si el proceso muere entre medias,
            // LogoPath encuentra el PNG antes que el JPEG y nunca queda "sin logo" con la casilla puesta.
            var destination = isPng ? _pngPath : _jpegPath;
            var obsolete = isPng ? _jpegPath : _pngPath;
            File.WriteAllBytes(destination, encoded);
            if (File.Exists(obsolete)) File.Delete(obsolete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudo guardar el logo personalizado", ex);
            return new BrandLogoImportResult(false, BrandLogoImportFailure.WriteFailed);
        }

        LogoChanged?.Invoke();
        return new BrandLogoImportResult(true, OriginalBytes: info.Length, StoredBytes: encoded.Length);
    }

    /// <summary>Borra el logo importado. Devuelve <c>false</c> si no había ninguno que borrar, para que
    /// quien llama no anuncie un cambio que no ha ocurrido.</summary>
    public bool Remove()
    {
        var removed = false;
        foreach (var path in new[] { _pngPath, _jpegPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning("No se pudo borrar el logo personalizado", ex);
            }
        }

        if (removed) LogoChanged?.Invoke();
        return removed;
    }

    /// <summary>Reescala manteniendo la proporción hasta que el lado mayor quepa en
    /// <see cref="MaxDimensionPixels"/>. Devuelve <c>null</c> cuando la imagen ya cabe: ampliarla solo
    /// añadiría peso e interpolación a algo que se va a dibujar a 32 px.</summary>
    private static SKBitmap? ScaleToFit(SKBitmap source)
    {
        var longestSide = Math.Max(source.Width, source.Height);
        if (longestSide <= MaxDimensionPixels) return null;

        var scale = MaxDimensionPixels / (double)longestSide;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var target = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(target);
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(source);
        // Mitchell: el remuestreo cúbico que Skia recomienda para reducir, sin el "halo" de sharpening de
        // CatmullRom ni el emborronado de una interpolación bilineal a esta escala de reducción.
        surface.Canvas.DrawImage(image, SKRect.Create(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        surface.Canvas.Flush();

        using var snapshot = surface.Snapshot();
        return SKBitmap.FromImage(snapshot);
    }

    /// <summary>Codifica en PNG y —solo si la imagen es totalmente opaca— también en JPEG, y devuelve el
    /// resultado más pequeño junto con qué formato ganó. Ver la nota de formato en la clase.</summary>
    private static (byte[] Data, bool IsPng) EncodeSmallest(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var pngBytes = png.ToArray();

        if (HasTransparency(bitmap)) return (pngBytes, true);

        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        var jpegBytes = jpeg.ToArray();
        return jpegBytes.Length < pngBytes.Length * JpegMustBeatPngBy ? (jpegBytes, false) : (pngBytes, true);
    }

    /// <summary>Recorre los píxeles ya reescalados (256×256 como mucho, así que es instantáneo) buscando
    /// uno no opaco. Mirar solo el <c>AlphaType</c> no vale: un PNG se decodifica con canal alfa aunque no
    /// lo use, y eso descartaría el candidato JPEG en todas las imágenes que más lo aprovechan.</summary>
    private static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque) return false;

        foreach (var pixel in bitmap.Pixels)
            if (pixel.Alpha != byte.MaxValue) return true;

        return false;
    }
}
