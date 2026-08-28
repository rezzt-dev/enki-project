using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Media;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Measures how wide a line will actually render once the live preview has hidden its markers and
/// restyled what is left — the number <see cref="MarkdownElementGenerator"/> needs to center or
/// right-align a line, since it has to reserve the leading space *before* the text is formatted.
///
/// Why it exists at all: AvaloniaEdit hands every visual line the same
/// <c>VisualLineTextParagraphProperties</c>, whose <c>TextAlignment</c> is hardcoded to
/// <c>Left</c> — there is no per-line alignment to set. The only way to move a line is to make its
/// first element wide enough to push the text across, which means knowing the text's width up front.
///
/// The measurement mirrors <see cref="MarkdownLivePreviewColorizer"/>'s decisions (heading size,
/// bold/italic typefaces, monospace inline code) so both agree on what is on screen; a run whose
/// styling the two disagree about would only shift the line by the difference between two typefaces
/// of the same string, never break it.
/// </summary>
internal static class MarkdownLineMeasurer
{
    /// <summary>Width of the visible text of <paramref name="lineText"/>, excluding the ranges the
    /// generator conceals (<paramref name="hidden"/>) and any trailing whitespace — text centred by a
    /// browser ignores trailing spaces too.</summary>
    /// <param name="from">Índice (relativo a <paramref name="lineText"/>) donde empezar a medir, o 0 para
    /// la línea entera. Lo usan las celdas de una tabla, que son trozos de línea.</param>
    /// <param name="to">Índice donde parar, o -1 para llegar al final de la línea.</param>
    /// <param name="weightOverride">Grosor con el que se dibujará el texto pese a lo que diga el bloque —
    /// la fila de encabezados de una tabla, que se pinta en seminegrita sin ser un encabezado Markdown.</param>
    public static double MeasureVisible(
        string lineText,
        int lineOffset,
        MdLine model,
        IReadOnlyList<(int Start, int End)> hidden,
        IReadOnlyList<(int Start, int End, MdSpanKind Kind)> htmlStyles,
        Func<int, int, bool> isCaretActive,
        MarkdownStyle style,
        int from = 0,
        int to = -1,
        FontWeight? weightOverride = null)
    {
        var n = to < 0 ? lineText.Length : Math.Min(to, lineText.Length);
        if (n <= from) return 0;

        var baseRun = BaseRunFor(model, style);
        if (weightOverride is { } weight) baseRun = baseRun with { Weight = weight };
        double total = 0;
        double pendingTrailing = 0;
        var i = from;

        while (i < n)
        {
            if (HiddenEnd(hidden, lineOffset + i) is { } skipTo)
            {
                i = Math.Max(i + 1, skipTo - lineOffset);
                continue;
            }

            var run = RunAt(lineOffset + i, model, htmlStyles, isCaretActive, style, baseRun);
            var j = i + 1;
            while (j < n && HiddenEnd(hidden, lineOffset + j) is null &&
                   RunAt(lineOffset + j, model, htmlStyles, isCaretActive, style, baseRun).Equals(run))
                j++;

            var segment = lineText[i..j];
            var formatted = Format(segment, run);
            // Trailing whitespace only counts once something visible follows it: a line ending in
            // spaces must center on its text, not on the invisible padding after it.
            total += pendingTrailing + formatted.Width;
            pendingTrailing = formatted.WidthIncludingTrailingWhitespace - formatted.Width;
            i = j;
        }

        return total;
    }

    /// <summary>Width of a replacement glyph the generator draws in place of a hidden marker (the "• "
    /// bullet), measured with the line's own base typeface.</summary>
    public static double MeasureGlyph(string glyph, MdLine model, MarkdownStyle style) =>
        Format(glyph, BaseRunFor(model, style)).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// Compone el texto visible de un trozo de línea en un <see cref="FormattedText"/> de párrafo: el
    /// mismo texto que mide <see cref="MeasureVisible"/> (sin los marcadores que el live preview oculta y
    /// con la tipografía que le toca a cada tramo), pero como un objeto que además se puede dibujar y que,
    /// al llevar <paramref name="maxWidth"/>, parte el texto en varias líneas cuando no cabe.
    ///
    /// <para>Es lo que hace posible que la celda de una tabla envuelva: el texto de una celda no se puede
    /// partir con los espaciadores del generador (un espaciador solo desplaza la primera línea visual, ver
    /// <see cref="WhitespaceFormattedElement"/>), así que una fila que envuelve se dibuja como un objeto
    /// propio a partir de estos párrafos — ver <see cref="MarkdownTableRowVisual"/>.</para>
    ///
    /// <para>Se mide y se dibuja siempre como si el cursor estuviera fuera, porque justamente eso es
    /// cuando una tabla se dibuja: con el cursor dentro se muestra el Markdown en crudo.</para>
    /// </summary>
    /// <param name="hidden">Rangos que no se dibujan. <c>Replacement</c> es el texto que ocupa el sitio del
    /// rango, o null para quitarlo sin más: lo usa la imagen de una celda envuelta, que se anuncia con el
    /// nombre de su archivo en vez de desaparecer sin dejar rastro de que la nota pedía algo ahí.</param>
    /// <param name="maxWidth">Ancho al que envolver, o 0 para no envolver.</param>
    public static FormattedText BuildVisible(
        string lineText,
        int lineOffset,
        MdLine model,
        IReadOnlyList<(int Start, int End, string? Replacement)> hidden,
        IReadOnlyList<(int Start, int End, MdSpanKind Kind)> htmlStyles,
        MarkdownStyle style,
        IBrush foreground,
        int from,
        int to,
        FontWeight? weightOverride,
        double maxWidth,
        TextAlignment alignment)
    {
        var n = to < 0 ? lineText.Length : Math.Min(to, lineText.Length);

        var baseRun = BaseRunFor(model, style);
        if (weightOverride is { } weight) baseRun = baseRun with { Weight = weight };

        var builder = new StringBuilder();
        var segments = new List<(int Start, int Length, Run Run)>();
        var i = Math.Max(0, from);

        while (i < n)
        {
            if (HiddenAt(hidden, lineOffset + i) is { } range)
            {
                if (!string.IsNullOrEmpty(range.Replacement))
                {
                    segments.Add((builder.Length, range.Replacement.Length, baseRun));
                    builder.Append(range.Replacement);
                }
                i = Math.Max(i + 1, range.End - lineOffset);
                continue;
            }

            var run = RunAt(lineOffset + i, model, htmlStyles, NeverActive, style, baseRun);
            var j = i + 1;
            while (j < n && HiddenAt(hidden, lineOffset + j) is null &&
                   RunAt(lineOffset + j, model, htmlStyles, NeverActive, style, baseRun).Equals(run))
                j++;

            segments.Add((builder.Length, j - i, run));
            builder.Append(lineText, i, j - i);
            i = j;
        }

        var formatted = new FormattedText(
            builder.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(baseRun.Family, baseRun.Style, baseRun.Weight), baseRun.Size, foreground)
        {
            // Cero es "sin límite" en FormattedText, que es justo lo que hace falta cuando la celda no
            // tiene que envolver.
            MaxTextWidth = maxWidth > 0 ? maxWidth : 0,
            TextAlignment = alignment
        };

        foreach (var (start, length, run) in segments)
        {
            if (length <= 0 || run.Equals(baseRun)) continue;
            formatted.SetFontTypeface(new Typeface(run.Family, run.Style, run.Weight), start, length);
            if (Math.Abs(run.Size - baseRun.Size) > 0.01) formatted.SetFontSize(run.Size, start, length);
        }

        return formatted;
    }

    private static bool NeverActive(int start, int end) => false;

    private readonly record struct Run(FontFamily Family, FontStyle Style, FontWeight Weight, double Size);

    private static Run BaseRunFor(MdLine model, MarkdownStyle style) => model.Kind switch
    {
        MdBlockKind.Heading => new Run(
            style.BodyFontFamily, FontStyle.Normal,
            model.HeadingLevel <= 2 ? FontWeight.Bold : FontWeight.SemiBold,
            style.BaseFontSize * MarkdownStyle.HeadingScale(model.HeadingLevel)),
        MdBlockKind.Blockquote => new Run(style.BodyFontFamily, FontStyle.Italic, FontWeight.Normal, style.BaseFontSize),
        MdBlockKind.CodeFence => new Run(style.MonoFontFamily, FontStyle.Normal, FontWeight.Normal, style.BaseFontSize),
        _ => new Run(style.BodyFontFamily, FontStyle.Normal, FontWeight.Normal, style.BaseFontSize)
    };

    private static Run RunAt(
        int offset,
        MdLine model,
        IReadOnlyList<(int Start, int End, MdSpanKind Kind)> htmlStyles,
        Func<int, int, bool> isCaretActive,
        MarkdownStyle style,
        Run baseRun)
    {
        var run = baseRun;

        foreach (var span in model.Spans)
        {
            if (span.ContentStart > offset || span.ContentEnd <= offset) continue;
            // Same rule the colorizer applies: a span the caret sits in shows its raw markers unstyled.
            if (isCaretActive(span.Start, span.End)) continue;
            run = Apply(span.Kind, run, style);
        }

        // HTML styling stays on regardless of the caret — the tags around it are concealed independently,
        // so there is no raw marker to fall back to.
        foreach (var (start, end, kind) in htmlStyles)
        {
            if (start <= offset && end > offset) run = Apply(kind, run, style);
        }

        return run;
    }

    private static Run Apply(MdSpanKind kind, Run run, MarkdownStyle style) => kind switch
    {
        MdSpanKind.Bold => run with { Weight = FontWeight.Bold },
        MdSpanKind.Italic => run with { Style = FontStyle.Italic },
        MdSpanKind.BoldItalic => run with { Style = FontStyle.Italic, Weight = FontWeight.Bold },
        MdSpanKind.InlineCode => run with { Family = style.MonoFontFamily },
        _ => run
    };

    private static FormattedText Format(string text, Run run) => new(
        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(run.Family, run.Style, run.Weight), run.Size, Brushes.Black);

    private static int? HiddenEnd(IReadOnlyList<(int Start, int End)> hidden, int offset)
    {
        foreach (var (start, end) in hidden)
        {
            if (start <= offset && end > offset) return end;
        }
        return null;
    }

    /// <summary>Como <see cref="HiddenEnd"/>, pero devolviendo el rango entero: quien compone el párrafo
    /// necesita además el texto que lo sustituye, si lo hay.</summary>
    private static (int Start, int End, string? Replacement)? HiddenAt(
        IReadOnlyList<(int Start, int End, string? Replacement)> hidden, int offset)
    {
        foreach (var range in hidden)
        {
            if (range.Start <= offset && range.End > offset) return range;
        }
        return null;
    }
}
