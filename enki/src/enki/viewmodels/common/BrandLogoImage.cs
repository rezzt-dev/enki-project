using System;
using System.IO;
using Avalonia.Media.Imaging;
using enki.Services;
using SkiaSharp;

namespace enki.ViewModels;

/// <summary>
/// Convierte los bytes del logo personalizado (<see cref="BrandLogoService.ReadLogoBytes"/>) en el
/// <see cref="Bitmap"/> que se va a pintar, reduciéndolo antes al tamaño en el que se verá.
///
/// <para><b>Por qué no basta con darle el archivo a Avalonia.</b> Un <c>Image</c> escala lo que le den con
/// el modo de interpolación del control, y el de por defecto no filtra: reducir 2000 px a 32 se convierte
/// en quedarse con uno de cada sesenta píxeles, que es exactamente el aspecto dentado que tenía el logo.
/// Aquí se reduce una sola vez con el remuestreo de calidad de Skia —que sí promedia todos los píxeles de
/// origen— y lo que llega al control ya está cerca de su tamaño final, así que el filtrado que queda por
/// hacer es mínimo. Las vistas ponen además <c>RenderOptions.BitmapInterpolationMode="HighQuality"</c>
/// para ese último tramo.</para>
///
/// <para>Reducir aquí y no al importar es lo que permite guardar el original intacto en disco (ver la nota
/// de <see cref="BrandLogoService"/>): la resolución de pintado se decide en cada arranque, y un monitor
/// nuevo o el zoom de interfaz al 200 % la aprovechan sin haber perdido nada por el camino.</para>
/// </summary>
internal static class BrandLogoImage
{
    /// <summary>Lado del logo del sidebar en píxeles físicos, en el peor caso: 24 px de cuadro × 2 de
    /// HiDPI × 2 del zoom de interfaz al 200 %. Se redujo de 128 a 96 para que el icono personalizado del
    /// panel lateral llegue al control ya cercano a su tamaño final y no haya que descartar píxeles de más.
    /// El peor caso de zoom (200 %) sigue cubierto durante el arranque con un vault de muestra en desarrollo.
    /// </summary>
    public const int SidebarPixels = 96;

    /// <summary>Lo mismo para la vista previa de 44 px de Ajustes › Apariencia.</summary>
    public const int PreviewPixels = 176;

    /// <summary>Decodifica <paramref name="data"/> dejando su lado <em>menor</em> en
    /// <paramref name="displayPixels"/> — el menor y no el mayor porque una imagen que no sea cuadrada se
    /// recorta al llenar el cuadro (<c>UniformToFill</c>), y es el lado corto el que tiene que dar la talla.
    /// Una imagen que ya es más pequeña se decodifica tal cual: ampliarla solo la emborronaría. Devuelve
    /// <c>null</c> si no hay bytes o si no hay forma de decodificarlos, caso en el que la vista cae al
    /// icono por defecto.</summary>
    public static Bitmap? Decode(byte[]? data, int displayPixels)
    {
        if (data is null || data.Length == 0) return null;

        try
        {
            var (width, height) = ReadPixelSize(data);
            if (width <= 0 || height <= 0) return null;

            var decodeWidth = ScaledDecodeWidth(width, height, displayPixels);
            using var stream = new MemoryStream(data, writable: false);
            return decodeWidth == 0
                ? new Bitmap(stream)
                : Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            LogService.Warning("No se pudo decodificar el logo personalizado", ex);
            return null;
        }
    }

    /// <summary>Ancho al que hay que pedirle la imagen a Skia para que su lado menor acabe en
    /// <paramref name="displayPixels"/>, o <c>0</c> si ya cabe y hay que decodificarla tal cual.</summary>
    internal static int ScaledDecodeWidth(int width, int height, int displayPixels)
    {
        var shortestSide = Math.Min(width, height);
        if (shortestSide <= displayPixels) return 0;

        return Math.Max(1, (int)Math.Round(width * (displayPixels / (double)shortestSide)));
    }

    /// <summary>Tamaño leído solo de la cabecera, sin decodificar píxeles. Devuelve (0, 0) si Skia no
    /// reconoce el formato — no es una imagen, o no es una que sepa abrir—, y entonces no hay logo que
    /// pintar. El flujo es propio a propósito: <see cref="SKCodec"/> toma posesión del que se le pasa y lo
    /// cierra al liberarse.</summary>
    internal static (int Width, int Height) ReadPixelSize(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var codec = SKCodec.Create(stream);
        return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
    }
}
