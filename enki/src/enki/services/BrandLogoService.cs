using System;
using System.IO;
using System.Linq;
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
    /// <summary>Más grande que <see cref="BrandLogoService.MaxSourceBytes"/>: un archivo que hay que releer
    /// y decodificar en cada arranque no puede ser de cualquier tamaño.</summary>
    TooLarge,
    /// <summary>Skia no supo decodificarlo: no es una imagen, o es un formato que no entiende (SVG, por
    /// ejemplo, que sí acepta el editor de notas pero aquí no aporta — el logo se rasteriza igual).</summary>
    UnsupportedFormat,
    /// <summary>La imagen se leyó bien, pero no se pudo copiar a <c>%AppData%/Enki</c>.</summary>
    WriteFailed
}

/// <summary>Lo que hay guardado ahora mismo: el tamaño del archivo y las dimensiones reales de la imagen,
/// que es lo que Ajustes enseña debajo de la vista previa ("1200 × 800 px · 1,4 MB").</summary>
public sealed record BrandLogoInfo(long Bytes, int PixelWidth, int PixelHeight);

/// <summary>Resultado de importar un logo: si salió bien y, cuando sí, qué quedó guardado.</summary>
public sealed record BrandLogoImportResult(
    bool Success,
    BrandLogoImportFailure Failure = BrandLogoImportFailure.None,
    BrandLogoInfo? Logo = null);

/// <summary>
/// Ajustes › Apariencia › Logo: guarda la imagen que el usuario elige para el brand header del sidebar
/// (<c>MainWindow.axaml</c>, esquina superior izquierda) y la deja lista para pintarse.
///
/// <para><b>Se guarda tal cual.</b> El archivo elegido se copia sin tocar un solo píxel: ni reescalado ni
/// recodificado. Enki es una aplicación local — el logo vive en el disco del usuario, no viaja por la red
/// ni se sincroniza — así que ahorrar unos cientos de kilobytes nunca compensa degradar la única imagen
/// que el usuario ha elegido a mano. Guardar el original es además lo que deja margen para pintarla bien
/// en cualquier pantalla y a cualquier zoom, en vez de congelar en disco la resolución que hoy nos parece
/// suficiente. Reducir de tamaño es cosa de quien la pinta, en memoria y al vuelo
/// (<c>ViewModels.BrandLogoImage</c>), no de lo que se archiva.</para>
///
/// <para><b>Formato.</b> El que trajera el original (PNG, JPG, WEBP, BMP, GIF, ICO — ver
/// <see cref="PickerPatterns"/>). La extensión con la que se guarda no sale del nombre del archivo de
/// origen sino de lo que Skia reconoce al leer su cabecera, para que un ".png" que en realidad era un JPEG
/// no deje en <c>%AppData%</c> un archivo que miente sobre su contenido.</para>
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
    /// <summary>Techo del archivo de origen. Con la imagen guardándose entera es lo único que la acota: se
    /// relee y se decodifica en cada arranque para pintar un cuadro de 32 px, y un TIFF de 200 MB sería
    /// pagar un pico de RAM absurdo por el mismo resultado. 64 MB deja pasar cualquier foto de móvil y
    /// cualquier exportación razonable.</summary>
    public const long MaxSourceBytes = 64L * 1024 * 1024;

    /// <summary>Nombre del archivo guardado, sin extensión: la que lleve depende del formato del original.</summary>
    private const string FileBaseName = "brand-logo";

    /// <summary>Extensiones que ofrece el diálogo de "elegir imagen". Es un subconjunto de las que acepta
    /// el editor de notas (<see cref="NoteImageAttachments"/>): fuera queda SVG, que Skia no decodifica
    /// como mapa de bits.</summary>
    public static readonly string[] PickerPatterns =
        ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif", "*.ico"];

    private readonly string _baseDirectory;
    private readonly SettingsService? _settingsService;
    private string _directory => _settingsService?.PreferenceDirectory ?? _baseDirectory;

    /// <summary>Se dispara tras importar o quitar el logo, para que el brand header del sidebar lo
    /// recargue en caliente. Mismo patrón de evento que el resto de servicios (<c>KanbanService.BoardChanged</c>,
    /// <c>SettingsService.SettingsChanged</c>): no basta con <c>SettingsChanged</c> porque reemplazar la
    /// imagen no cambia ningún ajuste, solo el archivo.</summary>
    public event Action? LogoChanged;

    /// <param name="storageDirectory">Sustituye la carpeta <c>%AppData%/Enki</c> por defecto — sin uso en
    /// la app real, pero deja que los tests escriban en un directorio desechable.</param>
    public BrandLogoService(string? storageDirectory = null)
    {
        _baseDirectory = storageDirectory ?? AppDataDirectory.Resolve();
        Directory.CreateDirectory(_baseDirectory);
    }

    public BrandLogoService(SettingsService settingsService) : this(Path.GetDirectoryName(settingsService.SettingsFilePath))
    {
        _settingsService = settingsService;
    }

    /// <summary>Ruta del logo importado, o <c>null</c> si el usuario no ha elegido ninguno todavía. Solo
    /// puede existir un <c>brand-logo.*</c> a la vez: <see cref="Import"/> borra el de la importación
    /// anterior cuando el formato nuevo no coincide. Se ordena por nombre para que, si una caída dejara
    /// dos archivos a medias, al menos siempre se elija el mismo.</summary>
    public string? LogoPath => StoredFiles().FirstOrDefault();

    public bool HasLogo => LogoPath is not null;

    /// <summary>Tamaño en disco y dimensiones reales del logo guardado, o <c>null</c> si no hay ninguno (o
    /// si el archivo dejó de ser legible). Las dimensiones salen de la cabecera, sin decodificar píxeles.</summary>
    public BrandLogoInfo? Describe()
    {
        if (LogoPath is not { } path) return null;

        try
        {
            var bytes = new FileInfo(path).Length;
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            return codec is null ? new BrandLogoInfo(bytes, 0, 0) : new BrandLogoInfo(bytes, codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudo leer el logo personalizado", ex);
            return null;
        }
    }

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

    /// <summary>Copia <paramref name="sourcePath"/> tal cual como el logo activo, reemplazando al anterior
    /// si lo había. Un fallo deja el logo previo intacto: la copia solo ocurre cuando la imagen ya se ha
    /// reconocido como tal.</summary>
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

        string extension;
        int width, height;
        try
        {
            // Solo la cabecera: basta para saber que es una imagen, de qué formato y de qué tamaño, sin
            // decodificar los píxeles de un archivo que vamos a copiar byte a byte de todos modos.
            using var stream = File.OpenRead(sourcePath);
            using var codec = SKCodec.Create(stream);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                return new BrandLogoImportResult(false, BrandLogoImportFailure.UnsupportedFormat);

            extension = ExtensionFor(codec.EncodedFormat);
            (width, height) = (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            LogService.Warning("No se pudo leer la imagen elegida para el logo", ex);
            return new BrandLogoImportResult(false, BrandLogoImportFailure.UnsupportedFormat);
        }

        long storedBytes;
        try
        {
            // Primero la copia nueva y después los restos del formato anterior: si el proceso muere entre
            // medias quedan dos archivos, pero nunca ninguno con la casilla de "personalizado" puesta.
            Directory.CreateDirectory(_directory);
            var destination = Path.Combine(_directory, FileBaseName + extension);
            // Reimportar el propio archivo guardado (el diálogo puede navegar hasta %AppData%) no es un
            // error: ya está donde tiene que estar, y File.Copy sobre sí mismo solo sabe lanzar.
            if (!PathsMatch(sourcePath, destination)) File.Copy(sourcePath, destination, overwrite: true);
            storedBytes = new FileInfo(destination).Length;

            foreach (var obsolete in StoredFiles().Where(path => !PathsMatch(path, destination)))
                File.Delete(obsolete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudo guardar el logo personalizado", ex);
            return new BrandLogoImportResult(false, BrandLogoImportFailure.WriteFailed);
        }

        LogoChanged?.Invoke();
        return new BrandLogoImportResult(true, Logo: new BrandLogoInfo(storedBytes, width, height));
    }

    /// <summary>Borra el logo importado. Devuelve <c>false</c> si no había ninguno que borrar, para que
    /// quien llama no anuncie un cambio que no ha ocurrido.</summary>
    public bool Remove()
    {
        var removed = false;
        foreach (var path in StoredFiles())
        {
            try
            {
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

    /// <summary>Los <c>brand-logo.*</c> que haya en la carpeta, en orden estable. En condiciones normales
    /// es cero o uno.</summary>
    private string[] StoredFiles()
    {
        try
        {
            var files = Directory.GetFiles(_directory, FileBaseName + ".*");
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            LogService.Warning("No se pudo listar el logo personalizado", ex);
            return [];
        }
    }

    /// <summary>Extensión que le corresponde al formato que Skia reconoció. Un formato exótico que sepa
    /// decodificar pero que no esté en el diálogo (HEIF de una foto de iPhone, AVIF) se guarda con su
    /// propio nombre en vez de rechazarse: si Skia lo abre, el sidebar lo va a pintar.</summary>
    private static string ExtensionFor(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => ".png",
        SKEncodedImageFormat.Jpeg => ".jpg",
        SKEncodedImageFormat.Webp => ".webp",
        SKEncodedImageFormat.Bmp => ".bmp",
        SKEncodedImageFormat.Gif => ".gif",
        SKEncodedImageFormat.Ico => ".ico",
        _ => "." + format.ToString().ToLowerInvariant()
    };

    private static bool PathsMatch(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
