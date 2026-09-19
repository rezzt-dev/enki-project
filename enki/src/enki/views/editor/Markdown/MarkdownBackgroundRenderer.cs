using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace enki.Views.Editor.Markdown;

/// <summary>Paints the structural chrome live-preview needs that plain text runs can't express:
/// the code-block shading, the blockquote rail and the horizontal-rule line.</summary>
public sealed class MarkdownBackgroundRenderer : IBackgroundRenderer
{
    private readonly LivePreviewState _state;
    private readonly MarkdownStyle _style;

    public MarkdownBackgroundRenderer(LivePreviewState state, MarkdownStyle style)
    {
        _state = state;
        _style = style;
    }


    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document is null) return;
        var width = textView.Bounds.Width;
        if (width <= 0) return;

        var rulePen = new Pen(_style.RuleLine, 1.5);
        var codeBorderPen = new Pen(_style.CodeBlockBorder, 1);

        // The code box is drawn OUTSIDE the text column on both sides, not inset into it: it starts
        // CodeBlockHorizontalPadding to the left of the first character's X and ends the same distance
        // past the wrap boundary. That's what guarantees "every glyph is inside the box", including the
        // continuation rows of a wrapped code line - those always start flush left at x=0 and always run
        // to the full wrap width, because Avalonia's text formatter ignores the wrap indentation
        // AvaloniaEdit computes for them (see MarkdownBlockTextFormatter). Any box
        // inset into [0, width] would therefore be overrun on both edges by every wrapped code line,
        // which is exactly the bug this replaced: an inset left edge with an inline spacer padding only
        // the FIRST row, and a right edge sitting exactly on the wrap boundary.
        //
        // Painting past TextView's own bounds needs its clipping relaxed by the same padding - see
        // EditorView.axaml.cs's UpdateCodeBlockBleed(), which also documents where that space comes from
        // (TextEditor.Padding on both sides). If a future Avalonia
        // version clips it anyway the block degrades to a full-width shaded band rather than breaking.
        // Medio píxel hacia DENTRO en cada lado, no el ajuste al medio píxel más cercano de Snap(): el
        // recorte que UpdateCodeBlockBleed() abre para el recuadro es exactamente [-pad, width+pad], así
        // que un trazo centrado en el borde de ese rango se queda a medias fuera (el derecho, entero) y el
        // borde no llega a verse. Centrado medio píxel dentro, el trazo de 1px cae completo en la banda de
        // píxeles inmediatamente interior y se ve nítido a ambos lados.
        var pad = MarkdownStyle.CodeBlockHorizontalPadding;
        var codeBoxLeft = Math.Round(-pad) + 0.5;
        var codeBoxRight = Math.Round(width + pad) - 0.5;

        // Fenced code blocks are painted as ONE rect around the whole block (like the preview's <pre>),
        // not per line: per-line fills leave hairline seams between visual lines and per-line borders read
        // as a stack of separate boxes. Fill and border are both deferred to the flush so the box closes
        // at the block's real top and bottom.
        //
        // Esquinas rectas, como el resto de la interfaz: el sistema de diseño no tiene radios de esquina
        // salvo en indicadores circulares (AGENTS.md, "Diseño visual").
        double? codeBlockTop = null;
        double codeBlockBottom = 0;

        void FlushCodeBlock()
        {
            if (codeBlockTop is not { } t) return;
            var top = Snap(t);
            var rect = new Rect(codeBoxLeft, top, codeBoxRight - codeBoxLeft, Snap(codeBlockBottom) - top);
            drawingContext.DrawRectangle(_style.CodeBlockBackground, codeBorderPen, rect);
            codeBlockTop = null;
        }

        foreach (var visualLine in textView.VisualLines)
        {
            var docLine = visualLine.FirstDocumentLine;
            var text = textView.Document.GetText(docLine);
            var fence = _state.GetFence(docLine.LineNumber);
            var model = MarkdownLineParser.Parse(text, docLine.Offset, fence);
            var top = visualLine.VisualTop - textView.VerticalOffset;

            if (model.Kind != MdBlockKind.CodeFence) FlushCodeBlock();

            switch (model.Kind)
            {
                case MdBlockKind.CodeFence:
                    codeBlockTop ??= top;
                    codeBlockBottom = top + visualLine.Height;
                    break;
                case MdBlockKind.Blockquote when _state.GetCallout(docLine.LineNumber) is { } callout:
                    DrawCallout(textView, drawingContext, visualLine, model, callout, top, width);
                    break;
                case MdBlockKind.Blockquote:
                    DrawQuoteRails(textView, drawingContext, visualLine, model, top);
                    break;
                case MdBlockKind.HorizontalRule when !_state.Intersects(docLine.Offset, docLine.EndOffset):
                {
                    var y = top + visualLine.Height / 2;
                    drawingContext.DrawLine(rulePen, new Point(0, y), new Point(width, y));
                    break;
                }

            }
        }

        FlushCodeBlock();

        DrawTables(textView, drawingContext);
        DrawSearchHighlights(textView, drawingContext);
    }

    /// <summary>Pinta una línea de un aviso: el fondo teñido de su tono y la barra de color a la
    /// izquierda, en el sitio exacto donde una cita normal llevaría su raya.
    ///
    /// Se dibuja línea a línea y no como un bloque entero (al contrario que el recuadro de un bloque de
    /// código) precisamente porque no lleva borde: sin trazo que cerrar no hay costuras que evitar, y así
    /// un aviso del que solo se ve un trozo en pantalla se pinta igual de bien, sin depender de que su
    /// primera o su última línea estén a la vista.</summary>
    private void DrawCallout(TextView textView, DrawingContext drawingContext, VisualLine visualLine,
        MdLine model, MdCalloutLine callout, double top, double width)
    {
        var accent = MarkdownCallouts.BrushOf(callout.Tone, _style);
        var left = QuoteRailX(textView, visualLine, model);
        // El fondo llega hasta el borde derecho de la columna: el aviso es un bloque, y recortarlo al
        // ancho de su texto lo convertiría en un resaltado de frases sueltas.
        drawingContext.FillRectangle(Tint(accent), new Rect(left, top, Math.Max(0, width - left), visualLine.Height));
        drawingContext.FillRectangle(accent,
            new Rect(Math.Round(left), top, MarkdownQuoteLayout.RailThickness, visualLine.Height));
    }

    /// <summary>El tono de fondo de un aviso: su propio color al 9 %. Lo justo para que el bloque se
    /// separe de la página sin que el texto de dentro pierda contraste, en tema claro y en oscuro.</summary>
    private static IBrush Tint(IBrush accent) => accent is ISolidColorBrush solid
        ? new ImmutableSolidColorBrush(solid.Color, 0.09)
        : Brushes.Transparent;

    /// <summary>La X donde empieza la raya de cita de <paramref name="model"/> — compartida por las citas
    /// normales y por los avisos, que es lo que hace que un aviso y una cita empiecen exactamente en la
    /// misma columna.</summary>
    private static double QuoteRailX(TextView textView, VisualLine visualLine, MdLine model)
    {
        if (visualLine.TextLines.Count == 0) return 0;
        var markerColumn = visualLine.GetVisualColumn(model.MarkerStart - visualLine.FirstDocumentLine.Offset);
        return visualLine.TextLines[0].GetDistanceFromCharacterHit(new CharacterHit(markerColumn, 0))
               - MarkdownQuoteLayout.ContentIndent(Math.Max(1, model.QuoteDepth)) - textView.HorizontalOffset;
    }

    private void DrawQuoteRails(TextView textView, DrawingContext drawingContext, VisualLine visualLine,
        MdLine model, double top)
    {
        if (model.QuoteDepth <= 0 || visualLine.TextLines.Count == 0) return;

        var markerColumn = visualLine.GetVisualColumn(model.MarkerStart - visualLine.FirstDocumentLine.Offset);
        var firstRow = visualLine.TextLines[0];
        var baseX = firstRow.GetDistanceFromCharacterHit(new CharacterHit(markerColumn, 0))
                    - MarkdownQuoteLayout.ContentIndent(model.QuoteDepth) - textView.HorizontalOffset;
        for (var level = 0; level < model.QuoteDepth; level++)
        {
            var x = Math.Round(baseX + MarkdownQuoteLayout.RailX(level));
            drawingContext.FillRectangle(_style.QuoteBar,
                new Rect(x, top, MarkdownQuoteLayout.RailThickness, visualLine.Height));
        }
    }

    /// <summary>Dibuja la rejilla de las tablas visibles: el fondo de la fila de encabezados, las líneas
    /// que separan filas y columnas, y el borde exterior. Es la contrapartida de los espaciadores que
    /// colocan el texto (<see cref="MarkdownElementGenerator"/>) — las dos capas leen la misma
    /// <see cref="MdTableGeometry"/>, así que la línea vertical cae siempre en el hueco entre dos celdas
    /// y nunca encima de una letra.
    ///
    /// <para>Las filas visibles de una misma tabla llegan seguidas (las líneas visuales de AvaloniaEdit
    /// están en orden), así que se acumulan y se dibujan de una vez: el borde exterior es un rectángulo de
    /// la tabla, no de cada fila. Y como de una tabla larga puede haber solo un trozo en pantalla, los
    /// bordes superior e inferior únicamente se dibujan si la primera/última fila de verdad están a la
    /// vista; los laterales se recortan solos con el desplazamiento.</para></summary>
    private void DrawTables(TextView textView, DrawingContext drawingContext)
    {
        if (!_state.Tables.HasTables || textView.Document is null) return;

        var pen = new Pen(_style.TableBorder, 1);
        var rows = new List<(MdTableRowKind Kind, int LineNumber, double Top, double Bottom)>();
        MdTable? currentTable = null;
        MdTableGeometry? currentGeometry = null;

        void Flush()
        {
            if (currentTable is { } table && currentGeometry is { } geometry && rows.Count > 0)
                DrawTable(drawingContext, pen, table, geometry, rows);
            rows.Clear();
            currentTable = null;
            currentGeometry = null;
        }

        foreach (var visualLine in textView.VisualLines)
        {
            var docLine = visualLine.FirstDocumentLine;
            if (_state.Tables.GetRow(docLine.LineNumber) is not { } row ||
                _state.IsTableActive(row.Table, textView.Document) ||
                _state.Tables.GetGeometry(row.Table, textView, _style, _state) is not { } geometry)
            {
                Flush();
                continue;
            }

            if (currentTable is not null && !ReferenceEquals(currentTable, row.Table)) Flush();
            currentTable = row.Table;
            currentGeometry = geometry;

            var top = visualLine.VisualTop - textView.VerticalOffset;
            rows.Add((row.Kind, docLine.LineNumber, top, top + visualLine.Height));
        }

        Flush();
    }

    private void DrawTable(DrawingContext drawingContext, Pen pen, MdTable table, MdTableGeometry geometry,
        List<(MdTableRowKind Kind, int LineNumber, double Top, double Bottom)> rows)
    {
        var left = Snap(0);
        var right = Snap(geometry.TotalWidth);
        var top = Snap(rows[0].Top);
        var bottom = Snap(rows[^1].Bottom);

        // El encabezado y la banda de la fila separadora comparten fondo: así el texto del encabezado no
        // acaba en seco justo debajo de sus letras, sino con el mismo aire que tiene por arriba.
        foreach (var row in rows)
        {
            if (row.Kind == MdTableRowKind.Body) continue;
            drawingContext.DrawRectangle(_style.TableHeaderBackground, null,
                new Rect(left, row.Top, geometry.TotalWidth, row.Bottom - row.Top));
        }

        for (var i = 0; i < rows.Count - 1; i++)
        {
            var y = Snap(rows[i].Bottom);
            drawingContext.DrawLine(pen, new Point(left, y), new Point(right, y));
        }

        for (var column = 1; column < geometry.ColumnX.Length; column++)
        {
            var x = Snap(geometry.SeparatorX(column));
            drawingContext.DrawLine(pen, new Point(x, top), new Point(x, bottom));
        }

        drawingContext.DrawLine(pen, new Point(left, top), new Point(left, bottom));
        drawingContext.DrawLine(pen, new Point(right, top), new Point(right, bottom));
        if (rows[0].LineNumber == table.StartLine)
            drawingContext.DrawLine(pen, new Point(left, top), new Point(right, top));
        if (rows[^1].LineNumber == table.EndLine)
            drawingContext.DrawLine(pen, new Point(left, bottom), new Point(right, bottom));
    }

    /// <summary>Lleva una coordenada al medio píxel más cercano: una línea de 1px dibujada en una
    /// coordenada entera se reparte entre dos píxeles y se ve gris y borrosa.</summary>
    private static double Snap(double value) => Math.Round(value) + 0.5;

    /// <summary>Shades every visible occurrence of the Buscar/Reemplazar search term, not just the one the
    /// native text selection already marks as "current" — convención habitual de la mayoría de editores de
    /// texto que faltaba aquí (análisis §4.2, Fase 8). Only scans the currently visible document lines
    /// (same cost class as the fence/blockquote loop above), not the whole document.</summary>
    private void DrawSearchHighlights(TextView textView, DrawingContext drawingContext)
    {
        var searchText = _state.SearchHighlightText;
        if (string.IsNullOrEmpty(searchText)) return;

        var comparison = _state.SearchHighlightMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
        var hasMatch = false;

        foreach (var visualLine in textView.VisualLines)
        {
            var docLine = visualLine.FirstDocumentLine;
            var text = textView.Document.GetText(docLine);
            var index = 0;
            while ((index = text.IndexOf(searchText, index, comparison)) != -1)
            {
                builder.AddSegment(textView, new SimpleSegment(docLine.Offset + index, searchText.Length));
                hasMatch = true;
                index += searchText.Length;
            }
        }

        if (!hasMatch) return;
        var geometry = builder.CreateGeometry();
        if (geometry is not null)
            drawingContext.DrawGeometry(_style.HighlightBackground, null, geometry);
    }
}
