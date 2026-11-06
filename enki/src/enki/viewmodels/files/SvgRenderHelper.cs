using System;
using System.IO;
using Avalonia.Media.Imaging;
using Svg.Skia;

namespace enki.ViewModels;

/// <summary>Rasteriza archivos SVG a <see cref="Bitmap"/> (Avalonia) usando <c>Svg.Skia</c>, que
/// carga el XML vectorial y lo pinta sobre un <c>SKBitmap</c> vía SkiaSharp. Paralelo a
/// <see cref="PdfRenderHelper"/> pero para SVG: ambos convierten un formato que Avalonia no decodifica
/// nativamente a un <c>Bitmap</c> pintado por software. Sin caché en disco — el renderizado es
/// instantáneo para los SVG típicos de un vault personal.</summary>
public static class SvgRenderHelper
{
    /// <summary>Rasteriza un SVG (datos XML en crudo) a un <see cref="Bitmap"/> de Avalonia con el
    /// ancho máximo indicado, manteniendo la proporción original. <paramref name="maxWidth"/> px.
    ///
    /// <para><paramref name="allowUpscale"/> gobierna qué hacer con un SVG más pequeño que ese ancho. Una
    /// miniatura del módulo Archivos lo quiere ampliado (la rejilla dibuja todas las tarjetas del mismo
    /// tamaño, y un icono diminuto perdido en el centro de una se vería como un fallo), pero una imagen
    /// dentro de una nota no: ahí ampliar un icono de 64 px hasta el ancho de la columna sería
    /// exactamente lo contrario de lo que el usuario escribió.</para></summary>
    public static Bitmap RenderSvg(byte[] svgData, int maxWidth, bool allowUpscale = true)
    {
        using var svg = new SKSvg();
        using var stream = new MemoryStream(svgData);
        svg.Load(stream);

        if (svg.Picture is null)
            throw new InvalidOperationException("SVG no contiene un Picture válido.");

        var picture = svg.Picture;
        var scale = maxWidth / picture.CullRect.Width;
        if (!allowUpscale && scale > 1) scale = 1;
        var width = (int)(picture.CullRect.Width * scale);
        var height = (int)(picture.CullRect.Height * scale);

        if (width <= 0) width = maxWidth;
        if (height <= 0) height = maxWidth;

        using var skBitmap = new SkiaSharp.SKBitmap(width, height);
        using var canvas = new SkiaSharp.SKCanvas(skBitmap);
        canvas.Clear(SkiaSharp.SKColors.Transparent);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);

        // SkiaSharp.SKBitmap → PNG byte stream → Avalonia Bitmap
        using var image = SkiaSharp.SKImage.FromBitmap(skBitmap);
        using var pngData = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var avaloniaStream = new MemoryStream(pngData.ToArray());
        return new Bitmap(avaloniaStream);
    }

    /// <summary>Rasteriza un SVG leyéndolo desde <paramref name="filePath"/> — mismo convenio que
    /// <see cref="RenderSvg(byte[], int)"/> pero abriendo el archivo directamente. Útil cuando el
    /// SVG no está cifrado (sesión protegida) y no hace falta pasarlo como buffer.</summary>
    public static Bitmap RenderSvgFromFile(string filePath, int maxWidth)
    {
        var data = File.ReadAllBytes(filePath);
        return RenderSvg(data, maxWidth);
    }
}