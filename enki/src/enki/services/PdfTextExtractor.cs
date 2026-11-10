using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PDFtoImage;
using SkiaSharp;

namespace enki.Services;

/// <summary>Candado único de PDFium en el proceso. PDFium no admite llamadas concurrentes y Enki lo usa por dos
/// caminos (el visor de Archivos a través de PDFtoImage y la lectura de texto del asistente con llamadas
/// directas), así que toda llamada a la biblioteca pasa por aquí.</summary>
internal static class PdfiumGate
{
    public static readonly object Sync = new();
}

/// <summary>Texto de una página de PDF: vacío o casi vacío en una página escaneada sin capa de texto.</summary>
internal sealed record PdfPageText(int Number, string Text)
{
    /// <summary>Menos caracteres útiles que esto se considera una página sin capa de texto (escaneada o solo
    /// imagen): su contenido solo se puede ver renderizándola.</summary>
    public const int MinimumMeaningfulChars = 16;

    public bool HasText => CountMeaningful(Text) >= MinimumMeaningfulChars;

    private static int CountMeaningful(string text)
    {
        var count = 0;
        foreach (var c in text) if (!char.IsWhiteSpace(c)) count++;
        return count;
    }
}

/// <summary>Lectura local de PDF para el asistente: texto por página con la capa de texto de PDFium (la misma
/// biblioteca nativa que ya distribuye PDFtoImage para el visor) y, para páginas escaneadas o con gráficos,
/// la página renderizada como JPEG. Trabaja sobre los bytes ya leídos —descifrados si el PDF vive en una
/// sesión protegida— y nunca escribe en disco.</summary>
// PDFium y PDFtoImage solo se usan en escritorio (Windows, Linux, macOS); ver PdfRenderHelper.
#pragma warning disable CA1416
internal static class PdfTextExtractor
{
    private const int PasswordError = 4;
    private static bool _initialized;

    /// <summary>Número de páginas y texto de las páginas pedidas (base 1, en orden), sin pasar de
    /// <paramref name="maxChars"/> caracteres acumulados.</summary>
    /// <exception cref="InvalidDataException">El PDF está dañado o protegido con contraseña.</exception>
    public static (int PageCount, List<PdfPageText> Pages, bool Truncated) ExtractText(byte[] pdf,
        Func<int, IEnumerable<int>> selectPages, int maxChars)
    {
        var pages = new List<PdfPageText>();
        var total = 0;
        var truncated = false;
        lock (PdfiumGate.Sync)
        {
            EnsureInitialized();
            var handle = GCHandle.Alloc(pdf, GCHandleType.Pinned);
            var document = IntPtr.Zero;
            try
            {
                document = Native.FPDF_LoadMemDocument64(handle.AddrOfPinnedObject(), (nuint)pdf.Length, IntPtr.Zero);
                if (document == IntPtr.Zero)
                    throw new InvalidDataException(Native.FPDF_GetLastError().Value == PasswordError
                        ? "The PDF is password-protected."
                        : "The PDF is damaged or not a valid PDF.");
                var pageCount = Native.FPDF_GetPageCount(document);
                foreach (var number in selectPages(pageCount))
                {
                    if (total >= maxChars)
                    {
                        truncated = true;
                        break;
                    }
                    var text = ReadPage(document, number - 1);
                    pages.Add(new PdfPageText(number, text));
                    total += text.Length;
                }
                return (pageCount, pages, truncated);
            }
            finally
            {
                if (document != IntPtr.Zero) Native.FPDF_CloseDocument(document);
                handle.Free();
            }
        }
    }

    /// <summary>Renderiza una página (base 1) como JPEG con su lado mayor en <paramref name="maxSide"/> píxeles
    /// y fondo blanco: lo que necesita un modelo con visión para leer una página escaneada o un gráfico.</summary>
    public static (byte[] Jpeg, int Width, int Height) RenderPage(byte[] pdf, int pageNumber, int maxSide)
    {
        lock (PdfiumGate.Sync)
        {
            using var stream = new MemoryStream(pdf, writable: false);
            var size = Conversion.GetPageSize(stream, pageNumber - 1, leaveOpen: true);
            stream.Position = 0;
            var landscape = size.Width >= size.Height;
            using var bitmap = Conversion.ToImage(stream, pageNumber - 1, leaveOpen: true, options: new RenderOptions(
                Width: landscape ? maxSide : null, Height: landscape ? null : maxSide, WithAspectRatio: true,
                BackgroundColor: SKColors.White));
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 78);
            return (data.ToArray(), bitmap.Width, bitmap.Height);
        }
    }

    private static string ReadPage(IntPtr document, int index)
    {
        var page = Native.FPDF_LoadPage(document, index);
        if (page == IntPtr.Zero) return string.Empty;
        var textPage = IntPtr.Zero;
        try
        {
            textPage = Native.FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero) return string.Empty;
            var count = Native.FPDFText_CountChars(textPage);
            if (count <= 0) return string.Empty;
            var buffer = new ushort[count + 1];
            var written = Native.FPDFText_GetText(textPage, 0, count, buffer);
            if (written <= 1) return string.Empty;
            var chars = new char[written - 1];
            for (var i = 0; i < chars.Length; i++) chars[i] = (char)buffer[i];
            return new string(chars);
        }
        finally
        {
            if (textPage != IntPtr.Zero) Native.FPDFText_ClosePage(textPage);
            Native.FPDF_ClosePage(page);
        }
    }

    private static void EnsureInitialized()
    {
        // FPDF_InitLibrary es idempotente en PDFium: PDFtoImage puede haberla llamado ya, o llamarla después.
        if (_initialized) return;
        Native.FPDF_InitLibrary();
        _initialized = true;
    }

    /// <summary>Convierte el texto de PDFium en texto compacto: saltos de línea normalizados, sin caracteres de
    /// control (PDFium marca con ellos guiones de corte y finales de línea generados), espacios repetidos
    /// reducidos a uno y como mucho una línea en blanco seguida.</summary>
    public static string Compact(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        var newlines = 0;
        var previous = '\0';
        foreach (var raw in text)
        {
            // "\r\n" es un solo salto de línea.
            var crlf = raw == '\n' && previous == '\r';
            previous = raw;
            if (crlf) continue;
            var c = raw == '\r' ? '\n' : raw;
            if (c == '\n')
            {
                pendingSpace = false;
                if (builder.Length > 0 && newlines < 2) builder.Append('\n');
                newlines++;
                continue;
            }
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0 && newlines == 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            newlines = 0;
            builder.Append(c);
        }
        while (builder.Length > 0 && builder[^1] == '\n') builder.Length--;
        return builder.ToString();
    }

    private static class Native
    {
        private const string Library = "pdfium";

        [DllImport(Library)] public static extern void FPDF_InitLibrary();
        [DllImport(Library)] public static extern CULong FPDF_GetLastError();
        [DllImport(Library)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, IntPtr password);
        [DllImport(Library)] public static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Library)] public static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Library)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Library)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Library)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Library)] public static extern void FPDFText_ClosePage(IntPtr textPage);
        [DllImport(Library)] public static extern int FPDFText_CountChars(IntPtr textPage);
        [DllImport(Library)] public static extern int FPDFText_GetText(IntPtr textPage, int start, int count, [Out] ushort[] result);
    }
}
#pragma warning restore CA1416
