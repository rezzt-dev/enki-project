using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Skia.Helpers;
using Avalonia.VisualTree;
using SkiaSharp;

namespace enki.Services.Export;

/// <summary>
/// Exportación de una nota a PDF y a PNG: precarga sus recursos (<see cref="NoteDocumentAssets"/>), la
/// compone (<see cref="NoteDocumentComposer"/>), la pagina (<see cref="DocumentPaginator"/>) y la dibuja.
///
/// <para><b>Un PDF de verdad, no una foto.</b> El PDF se dibuja con el backend PDF de Skia
/// (<see cref="SKDocument.CreatePdf(Stream, SKDocumentPdfMetadata)"/>), el mismo motor con el que Avalonia
/// pinta la ventana: los controles maquetados se dibujan directamente sobre el lienzo de cada página con
/// <see cref="DrawingContextHelper.RenderAsync(SKCanvas, Visual, Rect, Vector)"/>. El texto sale como texto
/// —se puede seleccionar, copiar y buscar, con las tipografías incrustadas y sin pixelarse al ampliar—, las
/// imágenes van a resolución de impresión, los enlaces se pueden pulsar y el documento lleva título, autor
/// de la aplicación y etiquetas en sus metadatos. Sin dependencias nuevas: SkiaSharp ya viene con
/// Avalonia.</para>
///
/// <para>La exportación a PNG de la nota usa la misma composición en una sola lámina, con la paleta del
/// tema activo, y la rasteriza con <see cref="VisualSnapshotRenderer"/> como el resto de vistas.</para>
/// </summary>
public static class NoteDocumentExporter
{
    /// <summary>Calidad JPEG con la que Skia recomprime las fotografías opacas del documento. Por debajo
    /// de 100 Skia guarda como JPEG las imágenes sin transparencia, que es lo que hace Word al guardar como
    /// PDF; con 92 no se aprecia pérdida ni en capturas con texto y el PDF de una nota con fotos pasa de
    /// decenas de megas a unos pocos.</summary>
    private const int ImageQuality = 92;

    /// <summary>Compone y pagina la nota y devuelve el PDF ya escrito en memoria.</summary>
    public static async Task<byte[]> CreatePdfAsync(NoteExportSource source, NotePageSetup page)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(page);

        var style = NoteDocumentStyle.ForPrint();
        var assets = await NoteDocumentAssets.LoadAsync(source, style, page.ContentWidth);
        var document = NoteDocumentComposer.Compose(source, style, assets, page.ContentWidth, page.ContentHeight);
        var pages = DocumentPaginator.Paginate(document.Blocks.Select(b => b.ToPagination()).ToList(), page.ContentHeight);
        return await RenderPdfAsync(document, pages, page, style, source);
    }

    /// <summary>La nota en una sola lámina tan alta como haga falta, al ancho de una hoja.</summary>
    public static async Task<ExportSnapshot> CreatePngAsync(NoteExportSource source, NotePageSetup page,
        ExportAppearance appearance, double scale)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(page);

        var style = NoteDocumentStyle.ForScreen(appearance);
        var assets = await NoteDocumentAssets.LoadAsync(source, style, page.ContentWidth);
        var document = NoteDocumentComposer.Compose(source, style, assets, page.ContentWidth, double.PositiveInfinity);
        return VisualSnapshotRenderer.Capture(BuildSheet(document, page, style), string.Empty, string.Empty, scale, appearance);
    }

    /// <summary>Los bloques uno debajo de otro, con su aire y los márgenes de la hoja alrededor.</summary>
    internal static Control BuildSheet(NoteDocument document, NotePageSetup page, NoteDocumentStyle style)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(page.MarginLeft, page.MarginTop, page.MarginRight, page.MarginBottom)
        };
        for (var index = 0; index < document.Blocks.Count; index++)
        {
            var block = document.Blocks[index];
            block.Visual.Margin = new Thickness(0, index == 0 ? 0 : block.SpaceBefore, 0, 0);
            content.Children.Add(block.Visual);
        }

        var sheet = new Border { Width = page.Width, Background = style.Paper, Child = content };
        sheet.Measure(new Size(page.Width, double.PositiveInfinity));
        sheet.Arrange(new Rect(0, 0, page.Width, Math.Max(page.Height, sheet.DesiredSize.Height)));
        return sheet;
    }

    private static async Task<byte[]> RenderPdfAsync(NoteDocument document, IReadOnlyList<IReadOnlyList<PageFragment>> pages,
        NotePageSetup page, NoteDocumentStyle style, NoteExportSource source)
    {
        using var stream = new MemoryStream();
        var now = DateTime.Now;
        var metadata = new SKDocumentPdfMetadata
        {
            Title = source.Title,
            Keywords = string.Join(", ", source.Tags),
            Creator = "Enki",
            Producer = "Enki",
            Creation = now,
            Modified = now,
            // Sin RasterDpi: Skia lo aplica como una escala inicial del lienzo de cada página, y
            // DrawingContextHelper.RenderAsync dibuja ignorando la matriz del lienzo, así que el documento
            // saldría encogido. No hace falta: aquí no hay efectos que rasterizar, y las imágenes se
            // incrustan siempre a su resolución propia.
            EncodingQuality = ImageQuality
        };

        using (var pdf = SKDocument.CreatePdf(stream, metadata)
                         ?? throw new InvalidOperationException("Skia no pudo crear el documento PDF."))
        {
            for (var index = 0; index < pages.Count; index++)
            {
                var canvas = pdf.BeginPage((float)page.Width, (float)page.Height);
                var layers = Layers(pages[index]);
                for (var layer = 0; layer < layers.Count; layer++)
                {
                    var pageNumber = layer == 0 ? (index + 1, pages.Count) : ((int, int)?)null;
                    var sheet = BuildPage(document, layers[layer], pageNumber, page, style);
                    try
                    {
                        canvas.Save();
                        await DrawingContextHelper.RenderAsync(canvas, sheet, new Rect(sheet.Bounds.Size), new Vector(96, 96));
                        canvas.Restore();
                        DrawLinks(canvas, sheet);
                    }
                    finally
                    {
                        // Los bloques vuelven a quedar sueltos: una tabla partida se coloca también en la
                        // capa o la página siguiente, y un control no puede tener dos padres.
                        foreach (var holder in sheet.Children.OfType<Canvas>()) holder.Children.Clear();
                        sheet.Children.Clear();
                    }
                }
                pdf.EndPage();
            }
            pdf.Close();
        }

        return stream.ToArray();
    }

    /// <summary>Margen de cortesía, a cada lado, del recorte de un trozo: una línea de 0,75 pt en el borde
    /// de la caja de texto (el borde de una tabla o de un bloque de código) no debe perder su mitad
    /// exterior.</summary>
    private const double ClipBleed = 2;

    /// <summary>
    /// La página entera como un árbol de controles: cada trozo, dentro de un contenedor que lo recorta a su
    /// franja [From, To) y colocado en su sitio de la caja de texto, más el número de página.
    ///
    /// <para>Se compone así —y no desplazando y recortando el lienzo de Skia antes de dibujar cada
    /// bloque— porque <see cref="DrawingContextHelper.RenderAsync(SKCanvas, Visual, Rect, Vector)"/> ignora
    /// la transformación del lienzo y no dibuja nada si el lienzo ya trae un recorte: todo lo que haya que
    /// mover o recortar se le tiene que dar hecho dentro del árbol visual. El bloque se dibuja entero,
    /// desplazado hacia arriba, y es el contenedor quien lo recorta: así una tabla o un bloque de código que
    /// continúa en la página siguiente se parte exactamente por el corte elegido, sin re-maquetarse
    /// distinto.</para>
    /// </summary>
    private static Canvas BuildPage(NoteDocument document, IReadOnlyList<PageFragment> fragments, (int Number, int Total)? pageNumber,
        NotePageSetup page, NoteDocumentStyle style)
    {
        var sheet = new Canvas { Width = page.Width, Height = page.Height };

        foreach (var fragment in fragments)
        {
            var visual = document.Blocks[fragment.Block].Visual;
            var holder = new Canvas
            {
                Width = document.Width + 2 * ClipBleed,
                Height = fragment.Height,
                ClipToBounds = true
            };
            Canvas.SetLeft(visual, ClipBleed);
            Canvas.SetTop(visual, -fragment.From);
            holder.Children.Add(visual);

            Canvas.SetLeft(holder, page.MarginLeft - ClipBleed);
            Canvas.SetTop(holder, page.MarginTop + fragment.Top);
            sheet.Children.Add(holder);
        }

        if (pageNumber is { } numbering) AddPageNumber(sheet, numbering.Number, numbering.Total, page, style);

        sheet.Measure(new Size(page.Width, page.Height));
        sheet.Arrange(new Rect(0, 0, page.Width, page.Height));
        return sheet;
    }

    /// <summary>Los trozos de una página repartidos en capas que se dibujan una encima de otra, de modo que
    /// ningún bloque aparezca dos veces en la misma capa. Solo pasa con una tabla que continúa: en la página
    /// siguiente se dibujan su fila de encabezados y, debajo, las filas que siguen, y las dos cosas son
    /// trozos del mismo control.</summary>
    private static List<List<PageFragment>> Layers(IReadOnlyList<PageFragment> fragments)
    {
        var layers = new List<List<PageFragment>>();
        foreach (var fragment in fragments)
        {
            var target = layers.FirstOrDefault(layer => layer.All(f => f.Block != fragment.Block));
            if (target is null) layers.Add(target = new List<PageFragment>());
            target.Add(fragment);
        }
        if (layers.Count == 0) layers.Add(new List<PageFragment>());
        return layers;
    }

    /// <summary>Número de página centrado en el pie, con su línea base a la distancia del borde a la que lo
    /// pone Word.</summary>
    private static void AddPageNumber(Canvas sheet, int number, int total, NotePageSetup page, NoteDocumentStyle style)
    {
        var label = new TextBlock
        {
            Text = string.Format(CultureInfo.InvariantCulture, "{0} / {1}", number, total),
            FontFamily = style.BodyFont,
            FontSize = Math.Round(style.FontSize * 0.75, 2),
            Foreground = style.InkMuted
        };
        label.Measure(Size.Infinity);
        var baseline = label.TextLayout.TextLines.Count > 0 ? label.TextLayout.TextLines[0].Baseline : label.DesiredSize.Height;
        Canvas.SetLeft(label, (page.Width - label.DesiredSize.Width) / 2);
        Canvas.SetTop(label, page.Height - page.FooterDistance - baseline);
        sheet.Children.Add(label);
    }

    /// <summary>Convierte cada enlace externo visible en la página en una zona clicable del PDF, sobre el
    /// rectángulo que ocupa su texto (uno por línea si el enlace se parte entre dos). Un enlace en la parte
    /// recortada de un trozo no está en esta página y no se anota.</summary>
    private static void DrawLinks(SKCanvas canvas, Canvas sheet)
    {
        foreach (var holder in sheet.Children.OfType<Canvas>())
        {
            var visible = holder.Bounds;
            foreach (var text in holder.GetVisualDescendants().OfType<TextBlock>())
            {
                var links = DocumentLinks.Of(text);
                if (links.Count == 0 || text.TranslatePoint(new Point(0, 0), sheet) is not { } origin) continue;

                foreach (var link in links)
                {
                    foreach (var rect in text.TextLayout.HitTestTextRange(link.Start, link.Length))
                    {
                        var area = rect.Translate(new Vector(origin.X, origin.Y)).Intersect(visible);
                        if (area.Width <= 0 || area.Height <= 0) continue;
                        canvas.DrawUrlAnnotation(
                            new SKRect((float)area.Left, (float)area.Top, (float)area.Right, (float)area.Bottom), link.Url);
                    }
                }
            }
        }
    }
}
