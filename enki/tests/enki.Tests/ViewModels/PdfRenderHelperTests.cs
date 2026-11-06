using System;
using System.IO;
using System.Text;
using enki.ViewModels;
using PDFtoImage;
using SkiaSharp;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Runtime smoke test for the PDFium/SkiaSharp integration behind the PDF preview
/// (docs/seccion-archivos.md §6) — a build-time compile check doesn't catch a native asset failing to
/// load or a malformed page count/render call, so this exercises the actual library against a real
/// (minimal) PDF file on disk.</summary>
#pragma warning disable CA1416 // Enki only ships Windows/Linux/macOS desktop builds — see PdfRenderHelper.cs.
public class PdfRenderHelperTests : IDisposable
{
    private readonly string _pdfPath = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N") + ".pdf");

    public PdfRenderHelperTests()
    {
        File.WriteAllBytes(_pdfPath, MinimalOnePagePdf());
    }

    public void Dispose()
    {
        if (File.Exists(_pdfPath)) File.Delete(_pdfPath);
    }

    [Fact]
    public void GetPageCount_ReadsAOnePagePdf()
    {
        Assert.Equal(1, PdfRenderHelper.GetPageCount(_pdfPath));
    }

    /// <summary>Same assertion as above but against a PDF built via SkiaSharp's own <see cref="SKDocument"/>
    /// writer instead of a hand-written minimal file — kept as a second, independently-produced input so a
    /// page-count bug specific to one PDF generation path (e.g. an off-by-one when a document actually has
    /// more than one page) can't hide behind the other passing.</summary>
    [Fact]
    public void GetPageCount_ReadsATwoPagePdf()
    {
        var path = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            using (var stream = File.Create(path))
            using (var document = SKDocument.CreatePdf(stream))
            {
                for (var i = 0; i < 2; i++)
                {
                    using var canvas = document.BeginPage(400, 300);
                    using var font = new SKFont(SKTypeface.Default, 20);
                    using var paint = new SKPaint { Color = SKColors.Black };
                    canvas.DrawText($"Page {i + 1}", 20, 40, SKTextAlign.Left, font, paint);
                    document.EndPage();
                }
                document.Close();
            }

            Assert.Equal(2, PdfRenderHelper.GetPageCount(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // The next two tests call PDFtoImage.Conversion directly rather than PdfRenderHelper — the helper's
    // final step wraps the rendered pixels into an Avalonia Bitmap, which needs a running
    // Avalonia.Application (platform render interface) that a plain xunit host never sets up, same as
    // every other Bitmap-producing path in this codebase (e.g. FileCardViewModel's image thumbnails) has
    // never been unit-testable. Calling PDFium/SkiaSharp directly still exercises the one genuinely new
    // and risky part — native PDF rasterization actually working at runtime on this platform — without
    // pulling in Avalonia.Headless just for that.

    [Fact]
    public void ConversionToImage_ByWidth_ProducesABitmapAtTheRequestedWidth()
    {
        using var stream = File.OpenRead(_pdfPath);
        using var bitmap = Conversion.ToImage(stream, page: 0, options: new RenderOptions(Width: 120, WithAspectRatio: true));

        Assert.Equal(120, bitmap.Width);
        Assert.True(bitmap.Height > 0);
    }

    [Fact]
    public void ConversionToImage_ByDpi_ProducesALargerBitmapAtAHigherDpi()
    {
        using var lowStream = File.OpenRead(_pdfPath);
        using var low = Conversion.ToImage(lowStream, page: 0, options: new RenderOptions(Dpi: 72));
        using var highStream = File.OpenRead(_pdfPath);
        using var high = Conversion.ToImage(highStream, page: 0, options: new RenderOptions(Dpi: 144));

        Assert.True(high.Width > low.Width);
    }

    /// <summary>The smallest commonly-used well-formed one-page PDF (a 200x200pt blank page, no
    /// content stream) — enough for PDFium to open, report a page count and rasterize.</summary>
    private static byte[] MinimalOnePagePdf()
    {
        const string pdf = """
            %PDF-1.4
            1 0 obj
            << /Type /Catalog /Pages 2 0 R >>
            endobj
            2 0 obj
            << /Type /Pages /Kids [3 0 R] /Count 1 >>
            endobj
            3 0 obj
            << /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>
            endobj
            trailer
            << /Size 4 /Root 1 0 R >>
            %%EOF
            """;
        return Encoding.ASCII.GetBytes(pdf);
    }
}
#pragma warning restore CA1416
