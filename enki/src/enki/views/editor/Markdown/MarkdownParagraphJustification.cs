using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Expande únicamente espacios ordinarios de filas ya envueltas y suficientemente llenas.
/// Copia los glifos antes de cambiar sus avances: el salto original y la caché de Avalonia
/// pueden compartir buffers con otras filas. La fila resultante conserva toda la geometría nativa.
/// </summary>
internal static class MarkdownParagraphJustification
{
    private const double MinimumFill = 0.85;
    private const int MinimumGaps = 3;
    private const double MaximumExtraEm = 0.20;
    private const double MaximumSpaceGrowth = 0.65;

    private readonly record struct Gap(int Run, int Glyph, double Limit);

    public static TextLine? Format(TextFormatter formatter, TextLine line, ITextRunConstructionContext context,
        MdLine paragraph, double width, TextParagraphProperties properties)
    {
        var visual = context.VisualLine;
        // El final del documento y cada salto real terminan el párrafo a estos efectos. No se
        // intenta unir líneas Markdown ni justificar una fila que termina por un salto obligatorio.
        if (properties.TextWrapping == TextWrapping.NoWrap || !double.IsFinite(width) || width <= 0 ||
            line.NewLineLength > 0 || line.TextLineBreak?.TextEndOfLine is not null ||
            line.FirstTextSourceIndex + line.Length >= visual.VisualLength ||
            line.HasOverflowed || line.Start != 0 || properties.Indent != 0 ||
            context.TextView.Options.WordWrapIndentation != 0 ||
            line.Width < width * MinimumFill || line.Width >= width)
            return null;

        var gaps = new List<Gap>();
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
                    if (local < 0 || local >= run.Text.Length || run.Text.Span[local] != ' ' ||
                        column + local <= line.FirstTextSourceIndex || column + local >= visibleEnd - 1 ||
                        glyph.GlyphAdvance <= 0)
                        continue;

                    var offset = visual.StartOffset + visual.GetRelativeOffset(column + local);
                    if (!IsProseSpace(context, paragraph, offset)) continue;
                    gaps.Add(new Gap(r, g, Math.Min(buffer.FontRenderingEmSize * MaximumExtraEm,
                        glyph.GlyphAdvance * MaximumSpaceGrowth)));
                }
            }
            column += run.Length;
        }

        if (gaps.Count < MinimumGaps) return null;
        var extra = (width - line.Width) / gaps.Count;
        foreach (var gap in gaps)
            if (extra > gap.Limit) return null;

        // Se entregan runs ya formados mediante APIs públicas: no se vuelve a medir el documento,
        // no se altera el shaping Unicode y no se accede al TextLineImpl privado de Avalonia.
        var runs = new TextRun[line.TextRuns.Count];
        var nextGap = 0;
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
                if (nextGap < gaps.Count && gaps[nextGap].Run == r && gaps[nextGap].Glyph == g)
                {
                    advance += extra;
                    nextGap++;
                }
                copy[g] = new GlyphInfo(glyph.GlyphIndex, glyph.GlyphCluster, advance, glyph.GlyphOffset);
            }
            runs[r] = new ShapedTextRun(copy, shaped.Properties);
        }

        // NoWrap impide que el aumento visual de los espacios vuelva a decidir los saltos.
        // Avalonia toma posesión de estos runs; WrappedRow conserva/dispondrá el salto original.
        return formatter.FormatLine(new ShapedRowSource(runs, line.FirstTextSourceIndex),
            line.FirstTextSourceIndex, width, new MarkdownBlockTextFormatter.RowProperties(properties, TextAlignment.Left));
    }

    private static bool IsProseSpace(ITextRunConstructionContext context, MdLine paragraph, int offset)
    {
        var document = context.Document;
        if (offset <= 0 || offset + 1 >= document.TextLength || document.GetCharAt(offset) != ' ' ||
            char.IsWhiteSpace(document.GetCharAt(offset - 1)) || char.IsWhiteSpace(document.GetCharAt(offset + 1)))
            return false;
        foreach (var span in paragraph.Spans)
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
}
