using System.IO;
using Avalonia.Media.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace enki.ViewModels;

/// <summary>Thin wrapper around PDFtoImage (PDFium + SkiaSharp) — the one place this section rasterizes a
/// PDF page to an Avalonia <see cref="Bitmap"/>, shared by the grid card's own thumbnail (first page,
/// low-res), the page-navigation rail's per-page thumbnails, and the full-size page shown in preview mode
/// (see <see cref="PdfDocumentViewModel"/>). Deliberately not the <c>NativeWebView</c> route
/// docs/seccion-archivos.md §6 originally flagged as an unverified spike (the app's own
/// <c>SessionGraphRenderer.cs</c> documents that Linux/WPE WebView backend as software-only and
/// crash-prone) — PDFium gives real, deterministic in-app rendering on every platform Enki runs on,
/// with no engine-availability gate needed.
///
/// Each call opens its own short-lived <see cref="FileStream"/> instead of caching the PDF's bytes in
/// memory: PDFium calls are internally lock-serialized by the library regardless (see its own docs on "no
/// parallelization support"), so nothing is gained by holding the file open or its bytes resident, and a
/// large scanned PDF never has to sit fully loaded in memory just to render one page.</summary>
// PDFtoImage's Conversion methods are annotated as unsupported on browser/wasm — Enki never targets that
// (it's a desktop WinExe shipping only Windows/Linux/macOS builds, all of which the analyzer already
// lists as supported), so CA1416 here is a known non-issue rather than something to design around.
#pragma warning disable CA1416
internal static class PdfRenderHelper
{
    public static int GetPageCount(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        return Conversion.GetPageCount(stream);
    }

    /// <summary>Renders <paramref name="pageIndex"/> (0-based) at a fixed pixel width, height following
    /// the page's own aspect ratio — used for thumbnails, where a consistent width matters more than a
    /// specific DPI.</summary>
    public static Bitmap RenderPageByWidth(string fullPath, int pageIndex, int width)
    {
        using var stream = File.OpenRead(fullPath);
        using var skBitmap = Conversion.ToImage(stream, page: pageIndex,
            options: new RenderOptions(Width: width, WithAspectRatio: true));
        return ToAvaloniaBitmap(skBitmap);
    }

    /// <summary>Renders <paramref name="pageIndex"/> (0-based) at a given DPI — used for the full-size
    /// preview page, where DPI (not a fixed pixel width) is what "zoom" actually means.</summary>
    public static Bitmap RenderPageByDpi(string fullPath, int pageIndex, int dpi)
    {
        using var stream = File.OpenRead(fullPath);
        using var skBitmap = Conversion.ToImage(stream, page: pageIndex, options: new RenderOptions(Dpi: dpi));
        return ToAvaloniaBitmap(skBitmap);
    }

    private static Bitmap ToAvaloniaBitmap(SKBitmap skBitmap)
    {
        using var stream = new MemoryStream();
        skBitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        stream.Position = 0;
        return new Bitmap(stream);
    }
}
#pragma warning restore CA1416
