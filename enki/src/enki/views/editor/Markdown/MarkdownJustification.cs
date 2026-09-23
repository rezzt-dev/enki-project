using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Justificación tipográfica de una fila ya partida por el formateador: reparte el hueco que le sobra a
/// la derecha hasta que el borde queda a ras de la columna, sin volver a medir el documento, sin alterar
/// el shaping Unicode y sin mover un solo salto de línea.
///
/// El reparto imita lo que hace un componedor profesional (InDesign, TeX) en vez de rendirse a la primera:
/// va relajando los límites por pasadas, de la menos visible a la más visible, y solo deja la fila en
/// bandera cuando ni con todo relajado se llega al borde.
/// <list type="number">
/// <item>Los espacios entre palabras crecen hasta <see cref="SpaceGrowthPreferred"/> veces su ancho.</item>
/// <item>Si no basta, se añade un poco de aire entre letras (<see cref="TrackingMaximumEm"/> del cuerpo
/// por letra) — imperceptible a esa escala y muchísimo menos llamativo que un río de espacios.</item>
/// <item>Si aún no basta, los espacios se estiran hasta <see cref="SpaceGrowthMaximum"/>.</item>
/// <item>Y si tampoco, se estiran sin tope: el borde derecho queda recto igualmente, que es lo que hace un
/// navegador y lo que espera quien pide texto justificado. Esta última pasada tiene una sola condición
/// (<see cref="MinimumFillToForce"/>): que la fila traiga texto suficiente como para que repartir el
/// blanco se lea como composición y no como un error.</item>
/// <item>Una fila sin un solo espacio donde repartir (una URL larguísima, texto CJK) se queda tal cual.</item>
/// </list>
///
/// Lo que nunca se toca: la última fila de un párrafo (un texto justificado tampoco la estira en papel),
/// el código en línea y las etiquetas HTML reveladas (su espaciado es literal), los espacios dobles
/// —marcarían el ritmo del autor— y cualquier cosa que no sea un espacio ordinario entre dos caracteres
/// visibles.
///
/// Se copian los glifos antes de cambiar sus avances: el salto original y la caché de Avalonia pueden
/// compartir buffers con otras filas.
/// </summary>
internal static class MarkdownJustification
{
    /// <summary>Crecimiento preferido de un espacio entre palabras, en múltiplos de su ancho natural —
    /// 1.5 deja el espacio en el 250 % de su tamaño, el máximo que un componedor considera "cómodo".</summary>
    private const double SpaceGrowthPreferred = 1.5;

    /// <summary>Crecimiento máximo absoluto antes de rendirse, ya con el interletraje agotado.</summary>
    private const double SpaceGrowthMaximum = 3.0;

    /// <summary>Interletraje máximo por letra, en fracción del cuerpo de su propia fuente. A 14,5 px son
    /// 0,58 px por letra: no se percibe como texto "espaciado", pero en una fila de 60 caracteres aporta
    /// ~35 px, que es justo lo que suele faltar cuando una palabra larga se va a la fila siguiente.</summary>
    private const double TrackingMaximumEm = 0.04;

    /// <summary>Ocupación mínima de la fila (fracción del ancho de la columna) para forzar el cuadre
    /// estirando los espacios por encima de <see cref="SpaceGrowthMaximum"/>. Por debajo no queda texto
    /// que componer: son dos palabras seguidas de un token gigantesco (una URL, una ruta), y repartir
    /// media columna entre ellas se lee como texto roto, no como texto justificado.</summary>
    private const double MinimumFillToForce = 0.5;

    /// <summary>Por debajo de esto la fila ya está a ras: repartir medio píxel no se ve y sí provoca
    /// repintados.</summary>
    private const double Epsilon = 0.05;

    /// <summary>Un hueco donde cabe reparto: qué glifo es y cuánto mide ahora.</summary>
    private readonly record struct Slot(int Run, int Glyph, double Natural);

    /// <summary>Bloques cuyo texto se justifica. Los encabezados quedan fuera a propósito: son de una
    /// fila casi siempre, y estirar el poco texto de un título se lee como un error, no como
    /// composición.</summary>
    public static bool IsJustifiableBlock(MdBlockKind kind) => kind is MdBlockKind.Paragraph
        or MdBlockKind.BulletList or MdBlockKind.OrderedList or MdBlockKind.TaskList or MdBlockKind.Blockquote;

    /// <summary>Si esta fila concreta admite justificación, con independencia del bloque al que pertenece:
    /// tiene que ser una fila intermedia de una línea que el formateador ha partido de verdad.</summary>
    public static bool CanJustifyRow(TextLine line, ITextRunConstructionContext context, double width,
        TextParagraphProperties properties)
    {
        // El final del documento y cada salto real terminan el párrafo a estos efectos. No se intenta unir
        // líneas Markdown ni justificar una fila que termina por un salto obligatorio.
        return properties.TextWrapping != TextWrapping.NoWrap && double.IsFinite(width) && width > 0 &&
               line.NewLineLength == 0 && line.TextLineBreak?.TextEndOfLine is null &&
               line.FirstTextSourceIndex + line.Length < context.VisualLine.VisualLength &&
               !line.HasOverflowed && line.Width > 0 && line.Width < width &&
               context.TextView.Options.WordWrapIndentation == 0;
    }

    /// <summary>Justifica una fila de un párrafo sin sangría y devuelve la fila ya formada, o null si esta
    /// fila se queda como está.</summary>
    public static TextLine? Format(TextFormatter formatter, TextLine line, ITextRunConstructionContext context,
        MdLine block, double width, TextParagraphProperties properties)
    {
        if (line.Start != 0 || properties.Indent != 0 || !CanJustifyRow(line, context, width, properties)) return null;
        var runs = BuildRuns(line, context, block, width);
        if (runs is null) return null;

        // NoWrap impide que el aumento visual de los espacios vuelva a decidir los saltos.
        // Avalonia toma posesión de estos runs; WrappedRow conserva/dispondrá el salto original.
        return formatter.FormatLine(new ShapedRowSource(runs, line.FirstTextSourceIndex),
            line.FirstTextSourceIndex, width,
            new MarkdownBlockTextFormatter.RowProperties(properties, TextAlignment.Left));
    }

    /// <summary>
    /// Los runs de <paramref name="line"/> con los avances ya repartidos para que la fila mida
    /// exactamente <paramref name="width"/>, o null si esta fila no se puede (o no se debe) justificar.
    /// Se entregan runs ya formados mediante APIs públicas: no se vuelve a medir el documento, no se
    /// altera el shaping Unicode y no se accede al TextLineImpl privado de Avalonia.
    /// </summary>
    public static TextRun[]? BuildRuns(TextLine line, ITextRunConstructionContext context, MdLine block, double width)
    {
        var deficit = width - line.Width;
        if (deficit <= Epsilon) return null;

        var gaps = new List<Slot>();
        var tracking = new List<Slot>();
        Collect(line, context, block, gaps, tracking);
        if (gaps.Count == 0) return null;

        double gapTotal = 0;
        foreach (var gap in gaps) gapTotal += gap.Natural;
        if (gapTotal <= 0) return null;

        double trackTotal = 0;
        foreach (var slot in tracking) trackTotal += slot.Natural;

        // Reparto por pasadas: espacios cómodos → interletraje → espacios al límite.
        var toSpaces = Math.Min(deficit, gapTotal * SpaceGrowthPreferred);
        var rest = deficit - toSpaces;
        var toTracking = Math.Min(rest, trackTotal * TrackingMaximumEm);
        rest -= toTracking;
        if (rest > Epsilon)
        {
            var stretched = Math.Min(rest, gapTotal * (SpaceGrowthMaximum - SpaceGrowthPreferred));
            toSpaces += stretched;
            rest -= stretched;
        }
        if (rest > Epsilon)
        {
            // Última pasada, sin tope. Solo se renuncia cuando en la fila no hay texto suficiente para que
            // el reparto se lea como composición; ver MinimumFillToForce.
            if (line.Width < width * MinimumFillToForce) return null;
            toSpaces += rest;
        }

        var deltas = new Dictionary<(int Run, int Glyph), double>(gaps.Count + tracking.Count);
        // Proporcional al ancho propio de cada hueco (y al cuerpo de cada letra): un espacio de una fuente
        // grande crece más que el de una nota al pie, que es como se reparte el blanco en composición.
        Distribute(deltas, gaps, gapTotal, toSpaces);
        if (toTracking > Epsilon) Distribute(deltas, tracking, trackTotal, toTracking);

        var runs = new TextRun[line.TextRuns.Count];
        for (var r = 0; r < runs.Length; r++)
        {
            if (line.TextRuns[r] is not ShapedTextRun shaped)
            {
                runs[r] = line.TextRuns[r];
                continue;
            }
            var original = shaped.ShapedBuffer;
            var copy = new ShapedBuffer(original.Text, original.Length, original.GlyphTypeface,
                original.FontRenderingEmSize, original.BidiLevel);
            for (var g = 0; g < original.Length; g++)
            {
                var glyph = original[g];
                var advance = glyph.GlyphAdvance;
                if (deltas.TryGetValue((r, g), out var extra)) advance += extra;
                copy[g] = new GlyphInfo(glyph.GlyphIndex, glyph.GlyphCluster, advance, glyph.GlyphOffset);
            }
            runs[r] = new ShapedTextRun(copy, shaped.Properties);
        }
        return runs;
    }

    /// <summary>Reparte <paramref name="amount"/> entre <paramref name="slots"/> en proporción a su peso, y
    /// le da al último lo que quede — que la fila caiga en el borde exacto, y no medio píxel antes, es lo
    /// que distingue un margen derecho recto de uno que "casi" lo está.</summary>
    private static void Distribute(Dictionary<(int, int), double> deltas, List<Slot> slots, double total, double amount)
    {
        double assigned = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var share = i == slots.Count - 1 ? amount - assigned : amount * (slot.Natural / total);
            assigned += share;
            deltas[(slot.Run, slot.Glyph)] = deltas.TryGetValue((slot.Run, slot.Glyph), out var previous)
                ? previous + share
                : share;
        }
    }

    /// <summary>Recorre los glifos de la fila una sola vez y clasifica cada uno: espacio de prosa donde
    /// repartir blanco, letra donde repartir interletraje, o nada.</summary>
    private static void Collect(TextLine line, ITextRunConstructionContext context, MdLine block,
        List<Slot> gaps, List<Slot> tracking)
    {
        var visual = context.VisualLine;
        var column = line.FirstTextSourceIndex;
        var visibleEnd = column + line.Length - line.TrailingWhitespaceLength;

        for (var r = 0; r < line.TextRuns.Count; r++)
        {
            var run = line.TextRuns[r];
            if (run is ShapedTextRun shaped)
            {
                var buffer = shaped.ShapedBuffer;
                var clusterStart = shaped.GlyphRun.Metrics.FirstCluster;
                for (var g = 0; g < buffer.Length; g++)
                {
                    var glyph = buffer[g];
                    var local = glyph.GlyphCluster - clusterStart;
                    if (local < 0 || local >= run.Text.Length || glyph.GlyphAdvance <= 0) continue;

                    var visualColumn = column + local;
                    // Ni el primer carácter de la fila ni el último admiten reparto: el blanco iría a parar
                    // fuera de la mancha de texto, no entre sus palabras.
                    if (visualColumn <= line.FirstTextSourceIndex || visualColumn >= visibleEnd - 1) continue;

                    var character = run.Text.Span[local];
                    var offset = visual.StartOffset + visual.GetRelativeOffset(visualColumn);
                    if (!IsProseOffset(context, block, offset)) continue;

                    if (character == ' ')
                    {
                        gaps.Add(new Slot(r, g, glyph.GlyphAdvance));
                    }
                    else if (!char.IsWhiteSpace(character) && IsClusterEnd(buffer, g, glyph.GlyphCluster))
                    {
                        // El peso es el cuerpo de la fuente: el interletraje se mide en ems, no en píxeles,
                        // así que una palabra en negrita grande y otra pequeña se separan lo proporcional.
                        tracking.Add(new Slot(r, g, buffer.FontRenderingEmSize));
                    }
                }
            }
            column += run.Length;
        }

        // El último glifo de la fila nunca lleva interletraje: ese blanco quedaría colgando tras la última
        // letra, justo contra el borde que se está intentando cuadrar.
        if (tracking.Count > 0) tracking.RemoveAt(tracking.Count - 1);
    }

    /// <summary>Si <paramref name="index"/> es el último glifo de su cluster. Añadir avance en mitad de un
    /// cluster partiría una ligadura o dejaría una tilde separada de su vocal.</summary>
    private static bool IsClusterEnd(ShapedBuffer buffer, int index, int cluster) =>
        index + 1 >= buffer.Length || buffer[index + 1].GlyphCluster != cluster;

    /// <summary>Si el carácter del documento en <paramref name="offset"/> forma parte de la prosa que se
    /// puede recomponer: ni código, ni marcado revelado, ni un espacio pegado a otro espacio.</summary>
    private static bool IsProseOffset(ITextRunConstructionContext context, MdLine block, int offset)
    {
        var document = context.Document;
        if (offset <= 0 || offset + 1 >= document.TextLength) return false;
        // El marcador del bloque ("1. ", "- [ ] ", "> ") no es prosa: su espacio es lo que separa el
        // marcador de su contenido, y estirarlo movería el texto de la primera fila respecto a la sangría
        // con la que se alinean todas las demás.
        if (offset < block.ContentStart) return false;
        if (document.GetCharAt(offset) == ' ' &&
            (char.IsWhiteSpace(document.GetCharAt(offset - 1)) || char.IsWhiteSpace(document.GetCharAt(offset + 1))))
            return false;

        foreach (var span in block.Spans)
        {
            if (offset < span.Start || offset >= span.End) continue;
            // El código y los destinos/marcadores revelados mantienen su espaciado literal.
            if (span.Kind is MdSpanKind.InlineCode or MdSpanKind.HtmlTag ||
                offset < span.ContentStart || offset >= span.ContentEnd)
                return false;
        }
        return true;
    }

    private sealed class ShapedRowSource(TextRun[] runs, int start) : ITextSource
    {
        private int _index;
        private int _offset = start;

        public TextRun? GetTextRun(int textSourceIndex)
        {
            if (_index >= runs.Length || textSourceIndex != _offset) return null;
            var run = runs[_index++];
            _offset += run.Length;
            return run;
        }
    }

    /// <summary>Coloca una fila ya justificada en una caja de <paramref name="paragraphWidth"/> alineada a
    /// la derecha, que es como <see cref="MarkdownBlockTextFormatter"/> consigue la sangría de listas y
    /// citas: como la fila mide exactamente el ancho de contenido, alinearla a la derecha la deja
    /// empezando justo en la sangría.</summary>
    public static TextLine? Position(TextFormatter formatter, TextRun[] runs, int firstTextSourceIndex,
        double paragraphWidth, TextParagraphProperties properties) =>
        formatter.FormatLine(new ShapedRowSource(runs, firstTextSourceIndex), firstTextSourceIndex, paragraphWidth,
            new MarkdownBlockTextFormatter.RowProperties(properties, TextAlignment.Right));
}
