using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using enki.Views.Editor.Markdown;
using SkiaSharp;

namespace enki.Services.Export;

/// <summary>Todo lo que hace falta para exportar una nota, sin que el exportador tenga que hablar con el
/// vault por su cuenta: el Markdown (el texto en vivo de la pestaña si está abierta), dónde está la nota
/// (para resolver sus imágenes relativas) y las dos lecturas que el viewmodel ya sabe hacer — la de bytes
/// en claro, que descifra sola una sesión protegida, y la resolución de <c>![[Nota]]</c>.</summary>
public sealed record NoteExportSource(
    string Title,
    string Markdown,
    IReadOnlyList<string> Tags,
    string? NotePath,
    string? VaultPath,
    Func<string, byte[]>? ReadBytes,
    Func<string, IReadOnlyList<string>?, NoteTransclusionResult>? ResolveTransclusion)
{
    public string? NoteDirectory => NotePath is null ? null : Path.GetDirectoryName(NotePath);
}

/// <summary>Una imagen lista para maquetar: el bitmap a resolución de impresión y su tamaño natural en
/// píxeles (el que tendría a 96 ppp, que es lo que el editor toma como «tamaño natural»).</summary>
internal sealed record DocumentImage(Bitmap Bitmap, double NaturalWidth, double NaturalHeight);

/// <summary>
/// Imágenes, fórmulas y resaltado de código de un documento exportado. Cargarlos es lo lento de exportar
/// —leer y decodificar fotos, descargar imágenes remotas, componer LaTeX, cargar gramáticas TextMate—, así
/// que <see cref="LoadAsync"/> lo hace en segundo plano antes de componer, y la composición
/// (<see cref="NoteDocumentComposer"/>, que crea controles y por tanto corre en el hilo de UI) solo consulta
/// lo ya cargado. Lo que la composición pida y no esté precargado (las imágenes de una nota transcluida) se
/// carga en el momento: es poco y evita tener que resolver transclusiones fuera del hilo de UI.
///
/// <para>Las imágenes se decodifican a resolución de impresión (≈300 ppp sobre el ancho al que se van a
/// dibujar), no a la de pantalla: una captura que en el editor se ve nítida tiene que verse nítida también
/// impresa. Pasan por <see cref="MarkdownImageCache.LoadUncached"/>, así que respetan exactamente lo mismo
/// que el editor: las rutas relativas, los adjuntos, la búsqueda por nombre en el vault, el descifrado de
/// sesiones protegidas, la preferencia de imágenes remotas y los topes contra imágenes desmesuradas.</para>
/// </summary>
internal sealed class NoteDocumentAssets
{
    /// <summary>Píxeles de imagen por punto de documento: 300 ppp.</summary>
    private const double PrintPixelsPerPoint = 300.0 / 72.0;

    /// <summary>Factor al que se componen las fórmulas respecto a su tamaño final.</summary>
    private const float MathOversampling = 4;

    private readonly Dictionary<string, DocumentImage?> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Latex, bool Display), DocumentImage?> _formulas = new();
    private readonly Dictionary<string, IReadOnlyList<List<CodeToken>>?> _code = new(StringComparer.Ordinal);
    private readonly NoteDocumentStyle _style;
    private readonly double _contentWidth;
    private readonly SKColor _ink;
    private CodeTokenizer? _tokenizer;
    private bool _tokenizerFailed;

    private NoteDocumentAssets(NoteDocumentStyle style, double contentWidth)
    {
        _style = style;
        _contentWidth = contentWidth;
        var ink = style.Ink is ISolidColorBrush brush ? brush.Color : Colors.Black;
        _ink = new SKColor(ink.R, ink.G, ink.B, ink.A);
    }

    /// <summary>Precarga en segundo plano todo lo que la nota referencia directamente.</summary>
    public static async Task<NoteDocumentAssets> LoadAsync(NoteExportSource source, NoteDocumentStyle style,
        double contentWidth)
    {
        var assets = new NoteDocumentAssets(style, contentWidth);
        await Task.Run(() => assets.Prefetch(source)).ConfigureAwait(true);
        return assets;
    }

    /// <summary>Imagen de <paramref name="src"/> escrita en una nota de <paramref name="noteDirectory"/>,
    /// o null si no se pudo cargar (el documento la sustituye por un hueco con su nombre).</summary>
    public DocumentImage? Image(NoteExportSource source, string? noteDirectory, string src, int requestedWidth)
    {
        var key = string.Concat(noteDirectory, "\0", src, "\0", requestedWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_images.TryGetValue(key, out var cached)) return cached;

        // Se decodifica al ancho máximo al que se puede llegar a dibujar: el pedido o, sin tamaño, el de la
        // caja de texto. Una imagen más pequeña que eso no se amplía (MarkdownImageCache no reescala hacia
        // arriba), así que una foto enorme ocupa en el PDF lo que necesita y un icono, lo que mide.
        var widthInPoints = requestedWidth > 0
            ? Math.Min(requestedWidth * _style.PointsPerPixel, _contentWidth)
            : _contentWidth;
        var decodeWidth = (int)Math.Ceiling(widthInPoints * PrintPixelsPerPoint);

        var context = new MarkdownImageContext
        {
            NoteDirectory = noteDirectory,
            VaultPath = source.VaultPath,
            ReadBytes = source.ReadBytes
        };
        var entry = MarkdownImageCache.LoadUncached(context, src, Math.Max(1, decodeWidth));
        DocumentImage? image = entry is { State: MarkdownImageState.Ready, Bitmap: { } bitmap }
                               && entry.NaturalWidth > 0 && entry.NaturalHeight > 0
            ? new DocumentImage(bitmap, entry.NaturalWidth, entry.NaturalHeight)
            : null;
        _images[key] = image;
        return image;
    }

    /// <summary>Fórmula LaTeX compuesta con CSharpMath —la misma que dibuja el editor— a
    /// <see cref="MathOversampling"/> veces su tamaño, para que al reducirla al de la línea quede a
    /// resolución de impresión. <see cref="DocumentImage.NaturalWidth"/> es ya el tamaño final en puntos.
    /// Null si el LaTeX no es válido; el documento enseña entonces su código.</summary>
    public DocumentImage? Formula(string latex, bool display)
    {
        if (_formulas.TryGetValue((latex, display), out var cached)) return cached;

        var size = (float)(_style.FontSize * (display ? 1.3 : 1.05));
        var png = LatexRenderService.RenderPng(latex, display, size * MathOversampling, _ink);
        DocumentImage? formula = null;
        if (png is not null)
        {
            try
            {
                using var stream = new MemoryStream(png, writable: false);
                var bitmap = new Bitmap(stream);
                formula = new DocumentImage(bitmap, bitmap.PixelSize.Width / MathOversampling,
                    bitmap.PixelSize.Height / MathOversampling);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                LogService.Warning("No se pudo decodificar una fórmula LaTeX para exportar la nota", ex);
            }
        }
        _formulas[(latex, display)] = formula;
        return formula;
    }

    /// <summary>Tramos coloreados de cada línea de un bloque de código, o null si el bloque no declara un
    /// lenguaje con gramática (se pinta entonces en un solo color, como en el editor).</summary>
    public IReadOnlyList<List<CodeToken>>? Code(IReadOnlyList<string> lines, string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || lines.Count == 0) return null;

        var key = language + "\0" + string.Join('\n', lines);
        if (_code.TryGetValue(key, out var cached)) return cached;

        IReadOnlyList<List<CodeToken>>? tokens = null;
        if (EnsureTokenizer() is { } tokenizer)
        {
            var limited = lines.Count > MarkdownCodeHighlighter.MaxBlockLines
                ? lines.Take(MarkdownCodeHighlighter.MaxBlockLines).ToList()
                : lines;
            // Sin las prisas del repintado del editor: un bloque largo en un documento exportado merece
            // colorearse entero, así que el presupuesto es generoso.
            tokens = tokenizer.TokenizeLines(limited, language, TimeSpan.FromSeconds(2));
        }
        _code[key] = tokens;
        return tokens;
    }

    private CodeTokenizer? EnsureTokenizer()
    {
        if (_tokenizer is not null || _tokenizerFailed) return _tokenizer;
        try
        {
            _tokenizer = new CodeTokenizer(_style.CodeTheme);
        }
        catch (Exception ex)
        {
            LogService.Warning("No se pudieron cargar las gramáticas para colorear el código de la nota exportada", ex);
            _tokenizerFailed = true;
        }
        return _tokenizer;
    }

    /// <summary>Recorre la nota con el mismo analizador que la composición y carga lo que vaya a pedir.
    /// No hace falta acertar al 100 %: lo que se escape aquí se cargará al componer.</summary>
    private void Prefetch(NoteExportSource source)
    {
        var text = NoteTagHelper.StripFrontMatter(source.Markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        var fences = MarkdownLineParser.ComputeFences(text);

        var codeLines = new List<string>();
        string? codeLanguage = null;
        var inCode = false;

        for (var i = 0; i < lines.Length && i < NoteDocumentComposer.MaxLines; i++)
        {
            fences.TryGetValue(i + 1, out var fence);
            if (fence.IsDelimiter)
            {
                if (inCode) Code(codeLines, codeLanguage);
                inCode = !inCode;
                codeLines = new List<string>();
                codeLanguage = fence.Language;
                continue;
            }
            if (fence.IsInside)
            {
                codeLines.Add(lines[i]);
                continue;
            }

            foreach (var span in MarkdownLineParser.Parse(lines[i], 0, fence).Spans)
            {
                if (span.Kind == MdSpanKind.Image && span.LinkTarget is { Length: > 0 } src
                                                  && !NoteAttachmentService.IsAttachmentTarget(src))
                    Image(source, source.NoteDirectory, src, span.ImageWidth);
                else if (span.Kind == MdSpanKind.Math)
                    Formula(lines[i][span.ContentStart..span.ContentEnd], span.IsDisplayMath);
            }
        }

        foreach (var block in MarkdownSpecialBlocks.Scan(text, fences))
        {
            if (block.Kind == MarkdownSpecialBlockKind.Math) Formula(block.Source, display: true);
        }
    }
}
