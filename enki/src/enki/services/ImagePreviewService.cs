using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using ImageMagick;

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
        var format = extension.ToLowerInvariant().TrimStart('.') switch
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
        var settings = new MagickReadSettings { Format = format, FrameCount = 1 };
        var info = new MagickImageInfo(bytes, settings);
        if ((long)info.Width * info.Height > 100_000_000)
            throw new InvalidDataException("La imagen supera el límite de 100 megapíxeles.");
        using var image = new MagickImage(bytes, settings);
        image.AutoOrient();
        originalSize = new PixelSize((int)image.Width, (int)image.Height);
        image.Thumbnail(new MagickGeometry((uint)maxSize, (uint)maxSize) { Greater = true });
        return image.ToByteArray(MagickFormat.Png);
    }
}
