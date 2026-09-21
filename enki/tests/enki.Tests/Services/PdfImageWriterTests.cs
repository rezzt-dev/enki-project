using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using enki.Services.Export;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="PdfImageWriter"/> — el escritor de PDF mínimo de T8. Son pruebas sobre los
/// bytes del archivo, no sobre píxeles: lo que hay que garantizar es que el PDF está bien formado
/// (cabecera, número de objetos, tabla xref con desplazamientos reales, tráiler) y que el tamaño de hoja
/// sale de la aritmética correcta. Que la imagen se vea bien es comprobación visual manual, y como tal
/// está en la checklist de QA.</summary>
public class PdfImageWriterTests
{
    private static PdfImagePage Page(int width = 4, int height = 3, double scale = 1) =>
        new(width, height, RandomishRgb(width * height * 3), scale);

    /// <summary>Datos con algo de variación: unos bytes todos iguales se comprimen tan bien que no
    /// distinguirían un flujo bien escrito de uno truncado.</summary>
    private static byte[] RandomishRgb(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(i * 37 % 251);
        return data;
    }

    private static string AsLatin1(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Fact]
    public void Create_WritesAPdfHeaderAndTrailer()
    {
        var text = AsLatin1(PdfImageWriter.Create([Page()]));

        Assert.StartsWith("%PDF-1.7\n", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Catalog", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Pages", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WithSeveralPages_DeclaresThemAllInTheTree()
    {
        var text = AsLatin1(PdfImageWriter.Create([Page(), Page(), Page()]));

        Assert.Contains("/Count 3", text, StringComparison.Ordinal);
        Assert.Contains("/Kids [ 3 0 R 6 0 R 9 0 R ]", text, StringComparison.Ordinal);
        // 2 objetos de cabecera + 3 por página.
        Assert.Contains("/Size 12", text, StringComparison.Ordinal);
    }

    /// <summary>La tabla <c>xref</c> es lo único del formato que no se puede "casi" acertar: un
    /// desplazamiento mal calculado da un archivo que ningún visor abre, y no se nota hasta abrirlo.</summary>
    [Fact]
    public void Create_XrefOffsets_PointAtTheRealObjects()
    {
        var pdf = PdfImageWriter.Create([Page(), Page()]);
        var text = AsLatin1(pdf);

        // "\nxref\n" y no "xref\n": lo segundo también casa dentro de "startxref".
        var xrefIndex = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        var startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length)..].Split('\n')[0]);
        Assert.Equal(xrefIndex, startxref);

        // Saltando "xref", la línea "0 N" y la entrada del objeto libre 0.
        var entries = text[xrefIndex..].Split('\n').Skip(3).Take(8).ToList();
        for (var i = 0; i < 8; i++)
        {
            var offset = int.Parse(entries[i][..10]);
            Assert.StartsWith($"{i + 1} 0 obj", text[offset..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Create_EmbedsThePixelsAsAFlateEncodedRgbImage()
    {
        var page = Page(5, 4);
        var pdf = PdfImageWriter.Create([page]);
        var text = AsLatin1(pdf);

        Assert.Contains("/Subtype /Image /Width 5 /Height 4", text, StringComparison.Ordinal);
        Assert.Contains("/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", text, StringComparison.Ordinal);

        // Y los bytes comprimidos son de verdad los píxeles: se descomprimen y se comparan.
        var streamStart = text.IndexOf("/Filter /FlateDecode", StringComparison.Ordinal);
        streamStart = text.IndexOf("stream\n", streamStart, StringComparison.Ordinal) + "stream\n".Length;
        var streamEnd = text.IndexOf("\nendstream", streamStart, StringComparison.Ordinal);

        using var compressed = new MemoryStream(pdf, streamStart, streamEnd - streamStart);
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        inflate.CopyTo(plain);

        Assert.Equal(page.Rgb, plain.ToArray());
    }

    /// <summary>Exportar a 2× tiene que dar la misma hoja con el doble de resolución, no una hoja del
    /// doble de tamaño.</summary>
    [Fact]
    public void PageSize_IgnoresTheRenderScale()
    {
        var standard = PdfImageWriter.PageSize(new PdfImagePage(960, 480, [], 1));
        var doubled = PdfImageWriter.PageSize(new PdfImagePage(1920, 960, [], 2));

        Assert.Equal(standard.Width, doubled.Width, 3);
        Assert.Equal(standard.Height, doubled.Height, 3);
        // 960 px lógicos a 96 ppp son 10 pulgadas = 720 puntos.
        Assert.Equal(720, standard.Width, 3);
    }

    /// <summary>Un tablero enorme supera el lado máximo de hoja que admite el formato (200"); la hoja se
    /// reduce en proporción en vez de generar un PDF que los visores rechazan.</summary>
    [Fact]
    public void PageSize_ClampsHugeSheets_KeepingTheAspectRatio()
    {
        var (width, height) = PdfImageWriter.PageSize(new PdfImagePage(80_000, 20_000, [], 1));

        Assert.Equal(14400, width, 3);
        Assert.Equal(3600, height, 3);
    }

    [Fact]
    public void Create_WithNoPages_Throws() =>
        Assert.Throws<ArgumentException>(() => PdfImageWriter.Create(new List<PdfImagePage>()));
}
