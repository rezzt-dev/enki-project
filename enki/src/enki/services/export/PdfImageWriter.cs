using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace enki.Services.Export;

/// <summary>Una página del PDF: una imagen RGB sin comprimir que ocupa la hoja entera.</summary>
/// <param name="PixelWidth">Ancho en píxeles de <paramref name="Rgb"/>.</param>
/// <param name="PixelHeight">Alto en píxeles de <paramref name="Rgb"/>.</param>
/// <param name="Rgb">Píxeles en orden RGB, 3 bytes por píxel, de arriba a abajo y de izquierda a derecha
/// (el orden que espera <c>/DeviceRGB</c> sin filtros de predicción).</param>
/// <param name="RenderScale">Píxeles renderizados por píxel lógico (1× o 2×), para traducir el tamaño en
/// píxeles al tamaño físico de la hoja sin que exportar a 2× duplique el tamaño del papel.</param>
public sealed record PdfImagePage(int PixelWidth, int PixelHeight, byte[] Rgb, double RenderScale);

/// <summary>Escritor de PDF mínimo: una hoja por imagen, sin dependencias nuevas (T8 de
/// .docs/funcionalidades-pendientes-priorizadas.md §4.4).
///
/// <para>El documento de la funcionalidad dejaba abierta la decisión de PDF entre (a) escribir el PDF a
/// mano a partir de las imágenes, (b) añadir una librería (QuestPDF, PdfSharp) y (c) quedarse en PNG más
/// "imprimir a PDF" del sistema. Se eligió (a): lo que hay que exportar aquí —un calendario, un tablero,
/// una nota maquetada— ya es una imagen rasterizada por Avalonia, así que lo único que aporta un PDF sobre
/// el PNG es la paginación y el tamaño de hoja, y eso cabe en este archivo. (b) traía una dependencia con
/// condiciones de licencia que revisar (§9, riesgo 4) para no usar ni una décima parte de sus capacidades,
/// y (c) no existe: Avalonia no expone diálogo de impresión del sistema.</para>
///
/// <para>El PDF generado es un PDF 1.7 clásico con tabla <c>xref</c> (no <em>cross-reference stream</em>):
/// más verboso pero legible, comprobable byte a byte en los tests y aceptado por cualquier visor. Cada
/// imagen va como un <c>XObject</c> <c>/DeviceRGB</c> de 8 bits con <c>/FlateDecode</c>, que es
/// exactamente el zlib de <see cref="ZLibStream"/>.</para></summary>
public static class PdfImageWriter
{
    /// <summary>Lado máximo de una hoja en el formato PDF: 200 pulgadas = 14400 puntos. Un tablero Kanban
    /// muy ancho exportado a 2× los supera con facilidad, así que la hoja se reduce proporcionalmente en
    /// vez de generar un PDF que los visores rechazan.</summary>
    private const double MaxPageSidePoints = 14400;

    /// <summary>Puntos PostScript por píxel lógico: el PDF mide en puntos de 1/72", y Avalonia trabaja en
    /// píxeles lógicos de 1/96".</summary>
    private const double PointsPerLogicalPixel = 72.0 / 96.0;

    public static byte[] Create(IReadOnlyList<PdfImagePage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ArgumentException("Un PDF necesita al menos una página.", nameof(pages));

        using var buffer = new MemoryStream();
        // Comentario binario de la segunda línea: marca el archivo como binario para las herramientas que
        // transfieren PDFs en modo texto. Es una convención del propio formato, no un adorno.
        Write(buffer, "%PDF-1.7\n%âãÏÓ\n");

        // 1 = catálogo, 2 = árbol de páginas, y después tres objetos por hoja (página, contenido, imagen).
        var objectCount = 2 + pages.Count * 3;
        var offsets = new long[objectCount + 1];

        var kids = new StringBuilder();
        for (var i = 0; i < pages.Count; i++) kids.Append(CultureInfo.InvariantCulture, $"{PageObject(i)} 0 R ");

        offsets[1] = buffer.Position;
        Write(buffer, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = buffer.Position;
        Write(buffer, $"2 0 obj\n<< /Type /Pages /Kids [ {kids.ToString().TrimEnd()} ] /Count {pages.Count} >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var (widthPoints, heightPoints) = PageSize(page);
            var content = Content(widthPoints, heightPoints);
            var compressed = Deflate(page.Rgb);

            offsets[PageObject(i)] = buffer.Position;
            Write(buffer,
                $"{PageObject(i)} 0 obj\n<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [ 0 0 {Number(widthPoints)} {Number(heightPoints)} ] " +
                $"/Resources << /XObject << /Im0 {ImageObject(i)} 0 R >> >> " +
                $"/Contents {ContentObject(i)} 0 R >>\nendobj\n");

            offsets[ContentObject(i)] = buffer.Position;
            Write(buffer, $"{ContentObject(i)} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            buffer.Write(content);
            Write(buffer, "\nendstream\nendobj\n");

            offsets[ImageObject(i)] = buffer.Position;
            Write(buffer,
                $"{ImageObject(i)} 0 obj\n<< /Type /XObject /Subtype /Image " +
                $"/Width {page.PixelWidth} /Height {page.PixelHeight} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode " +
                $"/Length {compressed.Length} >>\nstream\n");
            buffer.Write(compressed);
            Write(buffer, "\nendstream\nendobj\n");
        }

        var xrefOffset = buffer.Position;
        Write(buffer, $"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objectCount; i++)
            Write(buffer, offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

        Write(buffer, $"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        return buffer.ToArray();
    }

    private static int PageObject(int index) => 3 + index * 3;
    private static int ContentObject(int index) => 4 + index * 3;
    private static int ImageObject(int index) => 5 + index * 3;

    /// <summary>Tamaño de hoja en puntos. La imagen se dibuja a su tamaño <em>lógico</em> (dividiendo por
    /// <see cref="PdfImagePage.RenderScale"/>): exportar a 2× tiene que dar la misma hoja con el doble de
    /// resolución, no una hoja del doble de grande.</summary>
    internal static (double Width, double Height) PageSize(PdfImagePage page)
    {
        var scale = page.RenderScale <= 0 ? 1 : page.RenderScale;
        var width = page.PixelWidth / scale * PointsPerLogicalPixel;
        var height = page.PixelHeight / scale * PointsPerLogicalPixel;

        var overflow = Math.Max(width / MaxPageSidePoints, height / MaxPageSidePoints);
        if (overflow > 1)
        {
            width /= overflow;
            height /= overflow;
        }

        // Una hoja de cero puntos no la abre ningún visor; con una imagen de 1x1 píxel puede salir por
        // redondeo hacia abajo.
        return (Math.Max(width, 1), Math.Max(height, 1));
    }

    private static byte[] Content(double widthPoints, double heightPoints) =>
        Encoding.ASCII.GetBytes(
            "q\n" +
            $"{Number(widthPoints)} 0 0 {Number(heightPoints)} 0 0 cm\n" +
            "/Im0 Do\n" +
            "Q");

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    /// <summary>Los números del PDF no llevan separador de miles ni coma decimal: siempre cultura
    /// invariante, pase lo que pase con la del usuario.</summary>
    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static void Write(Stream stream, string text) => stream.Write(Encoding.Latin1.GetBytes(text));
}
