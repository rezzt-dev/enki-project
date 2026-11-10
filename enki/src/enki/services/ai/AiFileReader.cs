using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using enki.Models;

namespace enki.Services;

/// <summary>Cómo lee el asistente un archivo del vault que no es una nota.</summary>
public enum AiFileFormat
{
    /// <summary>Código y texto plano: se lee tal cual.</summary>
    Text,
    /// <summary>Texto por página con PDFium; las páginas escaneadas se pueden ver como imagen.</summary>
    Pdf,
    /// <summary>Word y PowerPoint (también con macros o de plantilla) y OpenDocument de texto o presentación.</summary>
    Document,
    /// <summary>Excel (XLSX, XLSM y plantillas) y OpenDocument (ODS): filas separadas por tabuladores, hoja a hoja.</summary>
    Spreadsheet,
    /// <summary>ZIP, JAR y NuGet: listado de rutas y tamaños.</summary>
    Archive,
    /// <summary>Imágenes raster y SVG: se envían reducidas a un modelo con visión.</summary>
    Image,
    /// <summary>Audio y vídeo: sin transcripción local, no se pueden leer.</summary>
    Media,
    /// <summary>Cualquier otra extensión: se lee como texto solo si su contenido lo es.</summary>
    Binary
}

/// <summary>Por qué un archivo del vault no se puede enviar al asistente.</summary>
public enum AiFileProblem
{
    None,
    /// <summary>Audio, vídeo o un formato binario que Enki no sabe leer.</summary>
    Unsupported,
    TooLarge
}

/// <summary>Una imagen que acompaña al resultado de una lectura (una foto, o una página de PDF renderizada).</summary>
public sealed record AiFileImage(string MimeType, byte[] Data);

/// <summary>Qué parte del archivo leer y cómo.</summary>
public sealed record AiFileReadOptions
{
    /// <summary>Carácter del texto extraído por el que empezar (para continuar una lectura truncada).</summary>
    public int Offset { get; init; }

    public int MaxChars { get; init; } = AiFileReader.DefaultMaxChars;

    /// <summary>Páginas de un PDF ("3", "2-5", "1,4,7-9"); todas si es <see langword="null"/>.</summary>
    public string? Pages { get; init; }

    /// <summary>Renderizar las páginas pedidas de un PDF como imagen, además de su texto.</summary>
    public bool PagesAsImages { get; init; }

    /// <summary>Lado mayor, en píxeles, de las imágenes que se devuelven.</summary>
    public int ImageSide { get; init; } = AiFileReader.StandardImageSide;

    /// <summary><see langword="false"/> para quedarse solo con texto y datos (un adjunto del mensaje, que viaja
    /// como texto): no se decodifica ni se renderiza ninguna imagen.</summary>
    public bool IncludeImages { get; init; } = true;
}

/// <summary>Resultado de una lectura: un tramo del texto extraído, cuánto queda, y las imágenes si las hay.</summary>
public sealed class AiFileExtract
{
    public required AiFileFormat Format { get; init; }

    /// <summary>El tramo de texto pedido (vacío en una imagen).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Caracteres del texto extraído entero (de las páginas pedidas, en un PDF).</summary>
    public int TotalChars { get; init; }

    public int Offset { get; init; }

    /// <summary>Desde dónde continuar, o <see langword="null"/> si el tramo llega al final.</summary>
    public int? NextOffset { get; init; }

    /// <summary>El archivo tiene más contenido del que Enki extrae de una vez (ver
    /// <see cref="AiFileReader.MaxExtractChars"/>); en un PDF, se puede seguir pidiendo otras páginas.</summary>
    public bool ExtractionLimited { get; init; }

    public int? PageCount { get; init; }

    public IReadOnlyList<int> PagesWithoutText { get; init; } = [];

    public IReadOnlyList<int> RenderedPages { get; init; } = [];

    public int? OriginalWidth { get; init; }
    public int? OriginalHeight { get; init; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }

    /// <summary>Fecha de captura EXIF de una foto, tal como la guarda la cámara.</summary>
    public string? Taken { get; init; }

    public IReadOnlyList<AiFileImage> Images { get; init; } = [];
}

/// <summary>Lectura de archivos del vault para el asistente, común a la herramienta <c>read_file</c> y a los
/// archivos que el usuario adjunta a un mensaje, para que las dos vías acepten y rechacen exactamente lo mismo.
/// Reutiliza los lectores de la sección Archivos (contenedores Office/OpenDocument, Magick.NET, PDFium) y nunca
/// interpreta ni ejecuta el contenido.
///
/// <para>Está pensado para gastar los tokens justos: el texto se extrae compacto (sin espacios repetidos ni
/// referencias de celda), se entrega por tramos con <see cref="AiFileReadOptions.Offset"/> y un total para que el
/// modelo decida si necesita más, un PDF se puede leer por páginas, y una imagen se reduce a un tamaño fijo
/// antes de enviarse. La extracción completa se guarda en una caché pequeña en memoria, así que leer el tramo
/// siguiente no vuelve a procesar el archivo.</para></summary>
public static class AiFileReader
{
    /// <summary>Tamaño máximo de un archivo de texto plano.</summary>
    public const long MaxTextBytes = 1_000_000;
    public const long MaxDocumentBytes = 32 * 1024 * 1024;
    public const long MaxPdfBytes = 64 * 1024 * 1024;
    public const long MaxImageBytes = 40 * 1024 * 1024;

    public const int DefaultMaxChars = 20_000;
    public const int MaxCharsLimit = 60_000;

    /// <summary>Texto que se extrae como mucho de un archivo de una vez.</summary>
    public const int MaxExtractChars = 1_000_000;

    public const int LowImageSide = 512;
    public const int StandardImageSide = 1024;
    public const int HighImageSide = 1568;

    /// <summary>Páginas de PDF que se renderizan como imagen en una sola lectura.</summary>
    public const int MaxRenderedPages = 3;

    private const int MaxPdfPages = 5000;
    private const int CacheEntries = 4;

    private static readonly LinkedList<(string Key, CachedText Value)> Cache = new();

    public static AiFileFormat FormatOf(VaultFile file) => file.Kind switch
    {
        VaultFileKind.Code => AiFileFormat.Text,
        VaultFileKind.Pdf => AiFileFormat.Pdf,
        VaultFileKind.Image => AiFileFormat.Image,
        VaultFileKind.Audio or VaultFileKind.Video => AiFileFormat.Media,
        _ => Path.GetExtension(file.FullPath).ToLowerInvariant() switch
        {
            var ext when OfficeTextExtractor.Supports(ext) || ext is ".odt" or ".odp" => AiFileFormat.Document,
            var ext when SpreadsheetTextExtractor.Supports(ext) => AiFileFormat.Spreadsheet,
            ".zip" or ".jar" or ".nupkg" => AiFileFormat.Archive,
            _ => AiFileFormat.Binary
        }
    };

    /// <summary>Lo que se sabe sin abrir el archivo: su formato por extensión y su tamaño.
    /// <paramref name="allowSniffing"/> admite una extensión desconocida pequeña, que se leerá como texto si su
    /// contenido lo es (la herramienta lo intenta; el selector de adjuntos, que no abre cada archivo, no).</summary>
    public static AiFileProblem Check(VaultFile file, bool allowSniffing = false)
    {
        var format = FormatOf(file);
        if (format == AiFileFormat.Media || (format == AiFileFormat.Binary && !allowSniffing)) return AiFileProblem.Unsupported;
        return file.SizeBytes > MaxBytes(format) ? AiFileProblem.TooLarge : AiFileProblem.None;
    }

    public static long MaxBytes(AiFileFormat format) => format switch
    {
        AiFileFormat.Text or AiFileFormat.Binary => MaxTextBytes,
        AiFileFormat.Pdf => MaxPdfBytes,
        AiFileFormat.Image => MaxImageBytes,
        _ => MaxDocumentBytes
    };

    public static VaultFile Describe(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return new VaultFile
        {
            FullPath = fullPath,
            ModifiedUtc = info.LastWriteTimeUtc,
            SizeBytes = info.Length,
            Kind = VaultFile.KindFromPath(fullPath)
        };
    }

    /// <summary>Lee el archivo según su formato. Pasa por <see cref="VaultService.ReadFileBytes"/>, que descifra
    /// los archivos de una sesión protegida.</summary>
    /// <exception cref="InvalidDataException">El contenido no corresponde a su formato, está dañado o protegido
    /// con contraseña, o una extensión desconocida no es texto. El mensaje es para el modelo, en inglés.</exception>
    /// <exception cref="ArgumentException">Las páginas pedidas no son válidas.</exception>
    public static AiFileExtract Read(VaultService vault, VaultFile file, AiFileReadOptions options)
    {
        var format = FormatOf(file);
        if (format == AiFileFormat.Media) throw new InvalidDataException($"\"{file.FileName}\" is audio or video; its content cannot be read.");
        var bytes = vault.ReadFileBytes(file.FullPath);
        try
        {
            return format == AiFileFormat.Image ? ReadImage(bytes, file, options) : ReadText(bytes, file, format, options);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException($"\"{file.FileName}\" could not be read: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is ImageMagick.MagickException or InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidDataException($"\"{file.FileName}\" could not be decoded as {Describe(format)}.", ex);
        }
    }

    /// <summary>Texto con el que viaja un archivo adjunto dentro de <c>&lt;enki_context&gt;</c>: el tramo leído y,
    /// si no cabe entero o es una imagen, una línea que explica qué falta y cómo obtenerlo.
    /// <paramref name="readFileArguments"/> son los argumentos exactos de <c>read_file</c> para ese archivo
    /// (<c>workspace="…" path="…"</c>), o <see langword="null"/> si el chat no tiene herramientas.</summary>
    public static string ToAttachmentText(AiFileExtract extract, string? readFileArguments)
    {
        var builder = new StringBuilder();
        var tools = readFileArguments is not null;
        if (extract.Format == AiFileFormat.Image)
        {
            builder.Append("[Image");
            if (extract.OriginalWidth is { } width && extract.OriginalHeight is { } height)
                builder.Append(CultureInfo.InvariantCulture, $", {width}×{height} px");
            builder.Append(tools
                ? $". Not included as text: call read_file with {readFileArguments} to look at it.]"
                : ". It cannot be shown in this chat: only these details are available.]");
            return builder.ToString();
        }
        if (extract.PageCount is { } pages) builder.Append(CultureInfo.InvariantCulture, $"[PDF, {pages} pages]\n");
        builder.Append(extract.Text);
        if (extract.PagesWithoutText.Count > 0)
            builder.Append("\n[Pages without a text layer (scanned or images only): ").Append(FormatPages(extract.PagesWithoutText))
                .Append(tools ? $". read_file with {readFileArguments} pages=\"…\" as_images=true shows them.]" : ".]");
        if (extract.NextOffset is { } next)
            builder.Append(CultureInfo.InvariantCulture, $"\n[… {extract.TotalChars - next} more characters not included")
                .Append(tools ? string.Create(CultureInfo.InvariantCulture, $"; read_file with {readFileArguments} offset={next} continues.]") : ".]");
        return builder.ToString();
    }

    public static string Describe(AiFileFormat format) => format switch
    {
        AiFileFormat.Pdf => "a PDF",
        AiFileFormat.Document => "a document",
        AiFileFormat.Spreadsheet => "a spreadsheet",
        AiFileFormat.Archive => "an archive",
        AiFileFormat.Image => "an image",
        _ => "text"
    };

    // ===================== Texto =====================

    private sealed record CachedText(string Text, bool Limited, int? PageCount, IReadOnlyList<int> PagesWithoutText,
        IReadOnlyList<int> SelectedPages);

    private static AiFileExtract ReadText(byte[] bytes, VaultFile file, AiFileFormat format, AiFileReadOptions options)
    {
        var key = string.Join('|', file.FullPath, file.SizeBytes.ToString(CultureInfo.InvariantCulture),
            file.ModifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture), options.Pages ?? string.Empty);
        var cached = FromCache(key);
        if (cached is null)
        {
            cached = Extract(bytes, file, format, options.Pages);
            AddToCache(key, cached);
        }

        var images = new List<AiFileImage>();
        var rendered = new List<int>();
        if (format == AiFileFormat.Pdf && options.IncludeImages)
        {
            // Se renderiza lo pedido o, si ninguna página pedida tiene texto (un escaneado), las primeras.
            var toRender = options.PagesAsImages ? cached.SelectedPages
                : cached.SelectedPages.Count > 0 && cached.PagesWithoutText.Count == cached.SelectedPages.Count ? cached.PagesWithoutText
                : [];
            foreach (var page in toRender.Take(MaxRenderedPages))
            {
                var (jpeg, _, _) = PdfTextExtractor.RenderPage(bytes, page, options.ImageSide);
                images.Add(new AiFileImage("image/jpeg", jpeg));
                rendered.Add(page);
            }
        }

        var (slice, offset, next) = Slice(cached.Text, options.Offset, options.MaxChars);
        return new AiFileExtract
        {
            Format = format,
            Text = slice,
            TotalChars = cached.Text.Length,
            Offset = offset,
            NextOffset = next,
            ExtractionLimited = cached.Limited,
            PageCount = cached.PageCount,
            PagesWithoutText = cached.PagesWithoutText,
            RenderedPages = rendered,
            Images = images
        };
    }

    private static CachedText Extract(byte[] bytes, VaultFile file, AiFileFormat format, string? pages)
    {
        bool limited;
        switch (format)
        {
            case AiFileFormat.Pdf:
                return ExtractPdf(bytes, pages);
            case AiFileFormat.Spreadsheet:
                var sheet = SpreadsheetTextExtractor.Read(bytes, file.FullPath, MaxExtractChars, out limited);
                return new CachedText(sheet, limited, null, [], []);
            case AiFileFormat.Document when OfficeTextExtractor.Supports(file.FullPath):
                // Word y PowerPoint, con títulos, listas, tablas, notas y comentarios (ver OfficeTextExtractor).
                return new CachedText(OfficeTextExtractor.Read(bytes, file.FullPath, MaxExtractChars, out limited), limited, null, [], []);
            case AiFileFormat.Document or AiFileFormat.Archive:
                var document = ContainerPreviewService.Read(bytes, file.FullPath, out limited);
                return new CachedText(CollapseBlankLines(document), limited, null, [], []);
            default:
                return new CachedText(DecodeText(bytes, file), false, null, [], []);
        }
    }

    private static CachedText ExtractPdf(byte[] bytes, string? pages)
    {
        IReadOnlyList<int> selected = [];
        var (pageCount, read, limited) = PdfTextExtractor.ExtractText(bytes, count =>
        {
            selected = ParsePages(pages, count);
            return selected;
        }, MaxExtractChars);
        var text = new StringBuilder();
        var empty = new List<int>();
        foreach (var page in read)
        {
            var compact = PdfTextExtractor.Compact(page.Text);
            if (!page.HasText)
            {
                empty.Add(page.Number);
                text.Append(CultureInfo.InvariantCulture, $"[page {page.Number}: no text layer]\n\n");
                continue;
            }
            text.Append(CultureInfo.InvariantCulture, $"[page {page.Number}]\n").Append(compact).Append("\n\n");
        }
        return new CachedText(text.ToString().TrimEnd(), limited, pageCount, empty, selected);
    }

    /// <summary>Texto UTF-8 (sin BOM). Una extensión de texto que contiene bytes nulos, o una desconocida que no
    /// es UTF-8 válido, no es texto.</summary>
    private static string DecodeText(byte[] bytes, VaultFile file)
    {
        var strict = FormatOf(file) == AiFileFormat.Binary;
        if (strict && bytes.Length > MaxTextBytes) throw new InvalidDataException($"\"{file.FileName}\" is too large to read as text.");
        string text;
        try
        {
            text = (strict ? new UTF8Encoding(false, throwOnInvalidBytes: true) : Encoding.UTF8).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException($"\"{file.FileName}\" is a binary file in a format Enki cannot read.");
        }
        if (text.Contains('\0', StringComparison.Ordinal))
            throw new InvalidDataException(strict
                ? $"\"{file.FileName}\" is a binary file in a format Enki cannot read."
                : $"\"{file.FileName}\" is not a text file.");
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>"3", "2-5" o "1,4,7-9" (base 1) contra el número real de páginas; todas si no se indica.</summary>
    internal static IReadOnlyList<int> ParsePages(string? pages, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(pages)) return Enumerable.Range(1, Math.Min(pageCount, MaxPdfPages)).ToList();
        var result = new SortedSet<int>();
        foreach (var part in pages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from))
                throw new ArgumentException($"'{part}' is not a page or a page range.");
            var to = from;
            if (bounds.Length == 2 && !int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out to))
                throw new ArgumentException($"'{part}' is not a page or a page range.");
            if (from < 1 || to < from || to > pageCount)
                throw new ArgumentException($"Pages must be between 1 and {pageCount}; '{part}' is not.");
            for (var page = from; page <= to && result.Count < MaxPdfPages; page++) result.Add(page);
        }
        return result.Count == 0 ? throw new ArgumentException("No pages were given.") : result.ToList();
    }

    internal static string FormatPages(IReadOnlyList<int> pages)
    {
        var parts = new List<string>();
        for (var i = 0; i < pages.Count; i++)
        {
            var start = pages[i];
            while (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1) i++;
            parts.Add(start == pages[i] ? start.ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{start}-{pages[i]}"));
        }
        return string.Join(',', parts);
    }

    /// <summary>Un tramo de como mucho <paramref name="maxChars"/> desde <paramref name="offset"/>. Si no llega al
    /// final, corta en el último salto de línea del último quinto del tramo, para no partir una fila o un
    /// párrafo, y nunca en medio de un par suplente.</summary>
    internal static (string Slice, int Offset, int? Next) Slice(string text, int offset, int maxChars)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        if (offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset])) offset--;
        var length = Math.Min(Math.Max(1, maxChars), text.Length - offset);
        if (offset + length < text.Length)
        {
            var newline = text.LastIndexOf('\n', offset + length - 1, length);
            if (newline >= offset + length * 4 / 5) length = newline + 1 - offset;
            else if (char.IsHighSurrogate(text[offset + length - 1])) length--;
        }
        var end = offset + length;
        return (text.Substring(offset, length), offset, end < text.Length ? end : null);
    }

    private static string CollapseBlankLines(string text)
    {
        var builder = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                if (++blank > 1 || builder.Length == 0) continue;
            }
            else blank = 0;
            builder.Append(trimmed).Append('\n');
        }
        return builder.ToString().TrimEnd();
    }

    private static CachedText? FromCache(string key)
    {
        lock (Cache)
        {
            for (var node = Cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Key != key) continue;
                Cache.Remove(node);
                Cache.AddFirst(node);
                return node.Value.Value;
            }
            return null;
        }
    }

    private static void AddToCache(string key, CachedText value)
    {
        lock (Cache)
        {
            Cache.AddFirst((key, value));
            while (Cache.Count > CacheEntries) Cache.RemoveLast();
        }
    }

    /// <summary>Vacía la caché de texto extraído (al cerrar o cambiar de vault, o al bloquear una sesión), para
    /// no conservar en memoria más de lo necesario el contenido de archivos de una sesión protegida.</summary>
    public static void ClearCache()
    {
        lock (Cache) Cache.Clear();
    }

    // ===================== Imágenes =====================

    private static AiFileExtract ReadImage(byte[] bytes, VaultFile file, AiFileReadOptions options)
    {
        if (!options.IncludeImages)
        {
            // Un adjunto solo necesita las dimensiones: no se decodifica la imagen entera.
            var size = ImagePreviewService.Probe(bytes, Path.GetExtension(file.FullPath));
            return new AiFileExtract { Format = AiFileFormat.Image, OriginalWidth = size?.Width, OriginalHeight = size?.Height };
        }
        var side = Math.Clamp(options.ImageSide, 64, HighImageSide);
        var jpeg = ImagePreviewService.EncodeForModel(bytes, Path.GetExtension(file.FullPath), side,
            out var original, out var encoded, out var taken);
        return new AiFileExtract
        {
            Format = AiFileFormat.Image,
            OriginalWidth = original.Width,
            OriginalHeight = original.Height,
            ImageWidth = encoded.Width,
            ImageHeight = encoded.Height,
            Taken = taken,
            Images = [new AiFileImage("image/jpeg", jpeg)]
        };
    }
}
