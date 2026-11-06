using System;
using Avalonia.Controls;
using AvaloniaEdit.Rendering;
using enki.Services;

namespace enki.Views.Editor.Markdown;

/// <summary>Ancho y alto a los que se dibuja una imagen, junto a la entrada de caché de la que sale (o de
/// la que saldrá, si todavía se está cargando).</summary>
internal readonly record struct MarkdownImageLayout(MarkdownImageEntry Entry, double Width, double Height);

/// <summary>
/// Decide a qué tamaño se dibuja un <c>![…](…)</c> y pide su bitmap a la caché. Vive aparte de
/// <see cref="MarkdownElementGenerator"/>, que es quien acaba construyendo el control, porque hay un
/// segundo interesado en la misma cuenta: una imagen dentro de la celda de una tabla decide el ancho de
/// su columna (<see cref="MarkdownTableLayout"/>), y las dos medidas tienen que salir del mismo sitio o
/// la imagen se saldría de la celda que se ha dimensionado para ella.
/// </summary>
internal static class MarkdownImageMeasurer
{
    /// <summary>Ancho del hueco que se dibuja mientras la imagen carga (o si no se pudo cargar).</summary>
    private const double PlaceholderWidth = 260;

    /// <summary>Reglas, por orden: manda el tamaño explícito de la nota (<c>![alt|300](…)</c>); si no lo
    /// hay, la imagen se dibuja a su tamaño natural; y en cualquier caso nunca más ancha que el espacio
    /// disponible, reduciendo el alto en la misma proporción para no deformarla. Mientras la imagen se
    /// carga (o si no se pudo cargar) el hueco mide lo que dos líneas de texto, que es poco suficiente
    /// para que se vea que hay algo ahí sin dar un salto grande cuando llegue la imagen de verdad.</summary>
    /// <param name="maxWidth">Techo adicional al ancho de la columna de texto — lo usa una celda de tabla,
    /// donde una imagen a ancho de columna dejaría la tabla inservible.</param>
    public static MarkdownImageLayout Measure(LivePreviewState state, TextView textView, MdSpan span,
        double maxWidth = double.PositiveInfinity)
    {
        var available = textView.Bounds.Width - 2 * MarkdownStyle.CodeBlockHorizontalPadding;
        if (double.IsNaN(available) || available <= 0) available = EditorPreferences.ColumnWidth;
        available = Math.Min(available, maxWidth);

        if (NoteAttachmentService.IsAttachmentTarget(span.LinkTarget ?? string.Empty))
            return new MarkdownImageLayout(new MarkdownImageEntry { State = MarkdownImageState.Ready },
                Math.Max(1, Math.Min(available, 420)), Math.Max(48, textView.DefaultLineHeight * 2));

        // Se decodifica a los píxeles físicos que la imagen va a ocupar: en una pantalla HiDPI, decodificar
        // al ancho lógico dejaría toda imagen grande visiblemente borrosa.
        var scaling = TopLevel.GetTopLevel(textView)?.RenderScaling ?? 1.0;
        var requested = span.ImageWidth > 0 ? span.ImageWidth : available;
        var decodeWidth = MarkdownImageResolver.QuantizeWidth(requested * scaling);

        var entry = MarkdownImageCache.Get(state.Images, span.LinkTarget ?? string.Empty, decodeWidth,
            () =>
            {
                // Una imagen recién cargada puede cambiar el ancho de la columna de la tabla en la que
                // esté, así que la geometría cacheada de las tablas deja de valer al mismo tiempo que el
                // hueco gris deja de dibujarse.
                state.Tables.InvalidateGeometry();
                textView.Redraw();
            });

        if (entry.State != MarkdownImageState.Ready || entry.NaturalWidth <= 0 || entry.NaturalHeight <= 0)
            return new MarkdownImageLayout(entry, Math.Min(available, PlaceholderWidth), textView.DefaultLineHeight * 2);

        var width = span.ImageWidth > 0 ? span.ImageWidth : Math.Min(entry.NaturalWidth, available);
        var height = span.ImageHeight > 0
            ? span.ImageHeight
            : width * entry.NaturalHeight / entry.NaturalWidth;

        if (width > available)
        {
            height *= available / width;
            width = available;
        }

        return new MarkdownImageLayout(entry, Math.Max(1, width), Math.Max(1, height));
    }
}
