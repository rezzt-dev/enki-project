using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using ImageMagick;
using SkiaSharp;
using Svg.Skia;

namespace enki.Services;

/// <summary>Primera página/fotograma con orientación EXIF y límites para no agotar la memoria del visor.</summary>
internal static class ImagePreviewService
{
    static ImagePreviewService()
    {
        // Las imágenes de sesiones cifradas no deben acabar en la caché temporal de ImageMagick.
        ResourceLimits.Memory = 256 * 1024 * 1024;
        ResourceLimits.Disk = 0;
        ResourceLimits.Thread = 2;
    }

    public static Bitmap Decode(byte[] bytes, int maxSize, string extension, out PixelSize originalSize)
    {
        using var stream = new MemoryStream(DecodePng(bytes, maxSize, extension, out originalSize));
        return new Bitmap(stream);
    }

    internal static byte[] DecodePng(byte[] bytes, int maxSize, string extension, out PixelSize originalSize)
    {
        var settings = new MagickReadSettings { Format = FormatFor(extension), FrameCount = 1 };
        var info = new MagickImageInfo(bytes, settings);
        if ((long)info.Width * info.Height > 100_000_000)
            throw new InvalidDataException("La imagen supera el límite de 100 megapíxeles.");
        using var image = new MagickImage(bytes, settings);
        image.AutoOrient();
        originalSize = new PixelSize((int)image.Width, (int)image.Height);
        image.Thumbnail(new MagickGeometry((uint)maxSize, (uint)maxSize) { Greater = true });
        return image.ToByteArray(MagickFormat.Png);
    }

    private static MagickFormat FormatFor(string extension) => extension.ToLowerInvariant().TrimStart('.') switch
    {
        "png" => MagickFormat.Png, "jpg" or "jpeg" => MagickFormat.Jpeg,
        "gif" => MagickFormat.Gif, "bmp" => MagickFormat.Bmp, "webp" => MagickFormat.WebP,
        "ico" => MagickFormat.Ico, "tif" or "tiff" => MagickFormat.Tiff,
        "avif" => MagickFormat.Avif, "heic" or "heif" => MagickFormat.Heic,
        "tga" => MagickFormat.Tga, "psd" => MagickFormat.Psd, "jxl" => MagickFormat.Jxl,
        "pbm" => MagickFormat.Pbm, "pgm" => MagickFormat.Pgm, "ppm" => MagickFormat.Ppm,
        "qoi" => MagickFormat.Qoi,
        _ => throw new InvalidDataException("Formato de imagen no admitido.")
    };

    /// <summary>Dimensiones de una imagen raster leyendo solo su cabecera; <see langword="null"/> en un SVG (habría
    /// que rasterizarlo) o si el formato no las expone.</summary>
    internal static PixelSize? Probe(byte[] bytes, string extension)
    {
        if (extension.TrimStart('.').Equals("svg", StringComparison.OrdinalIgnoreCase)) return null;
        var info = new MagickImageInfo(bytes, new MagickReadSettings { Format = FormatFor(extension), FrameCount = 1 });
        return info.Width > 0 && info.Height > 0 ? new PixelSize((int)info.Width, (int)info.Height) : null;
    }

    /// <summary>Imagen lista para un modelo con visión: primera página o fotograma, orientada según EXIF, con su
    /// lado mayor reducido a <paramref name="maxSide"/> píxeles (nunca ampliada), transparencias sobre blanco,
    /// sin metadatos y en JPEG. Es lo que fija el coste en tokens de una imagen, que depende de sus píxeles y
    /// no del tamaño del archivo original. <paramref name="taken"/> es la fecha de captura EXIF, si la hay.</summary>
    internal static byte[] EncodeForModel(byte[] bytes, string extension, int maxSide, out PixelSize originalSize,
        out PixelSize encodedSize, out string? taken)
    {
        taken = null;
        if (extension.TrimStart('.').Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            using var svg = RasterizeSvg(bytes, maxSide, allowUpscale: true);
            using var raster = SKImage.FromBitmap(svg);
            using var png = raster.Encode(SKEncodedImageFormat.Png, 100);
            bytes = png.ToArray();
            extension = ".png";
        }
        var settings = new MagickReadSettings { Format = FormatFor(extension), FrameCount = 1 };
        var info = new MagickImageInfo(bytes, settings);
        if ((long)info.Width * info.Height > 100_000_000)
            throw new InvalidDataException("The image exceeds the 100-megapixel limit.");
        using var image = new MagickImage(bytes, settings);
        if (image.GetExifProfile()?.GetValue(ExifTag.DateTimeOriginal)?.Value is { Length: > 0 } original)
            taken = original;
        image.AutoOrient();
        originalSize = new PixelSize((int)image.Width, (int)image.Height);
        image.Thumbnail(new MagickGeometry((uint)maxSide, (uint)maxSide) { Greater = true });
        if (image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
        }
        image.Strip();
        image.Quality = 80;
        image.Format = MagickFormat.Jpeg;
        encodedSize = new PixelSize((int)image.Width, (int)image.Height);
        return image.ToByteArray();
    }

    /// <summary>Rasteriza un SVG a <paramref name="maxWidth"/> píxeles de ancho conservando la proporción. Lo
    /// comparten el visor de Archivos (<c>SvgRenderHelper</c>) y la lectura de imágenes del asistente.</summary>
    internal static SKBitmap RasterizeSvg(byte[] svgData, int maxWidth, bool allowUpscale)
    {
        using var svg = new SKSvg();
        using var stream = new MemoryStream(svgData);
        svg.Load(stream);
        var picture = svg.Picture ?? throw new InvalidOperationException("SVG no contiene un Picture válido.");
        var scale = maxWidth / picture.CullRect.Width;
        if (!allowUpscale && scale > 1) scale = 1;
        var width = (int)(picture.CullRect.Width * scale);
        var height = (int)(picture.CullRect.Height * scale);
        if (width <= 0) width = maxWidth;
        if (height <= 0) height = maxWidth;
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        return bitmap;
    }
}
