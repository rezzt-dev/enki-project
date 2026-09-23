using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Sangría de listas y citas dentro del formateo real. Avalonia 12.1 ignora Indent: se calcula el salto
/// con el ancho restante y se alinea esa fila nativa en una caja de su ancho más la sangría.
/// Así Draw, GetTextBounds y ambos sentidos del hit-testing comparten las mismas métricas.
/// También justifica suavemente los párrafos registrados, conservando sus saltos nativos.
/// </summary>
internal sealed class MarkdownBlockTextFormatter(TextFormatter inner) : TextFormatter
{
    private sealed class BlockLayout(int contentOffset, double firstLineIndent)
    {
        public int ContentOffset { get; } = contentOffset;
        public double FirstLineIndent { get; } = firstLineIndent;
        public double Indent { get; set; }
    }

    private static readonly ConditionalWeakTable<VisualLine, BlockLayout> Blocks = new();

    /// <summary>Las líneas cuyo texto se justifica, con su análisis ya hecho. A diferencia de
    /// <see cref="Blocks"/> —que solo tienen listas y citas, las que necesitan sangría— aquí entra
    /// cualquier bloque de prosa, incluidos los párrafos sueltos.</summary>
    private static readonly ConditionalWeakTable<VisualLine, MdLine> Justifiable = new();

    // AvaloniaEdit 12.0 no expone un setter para el formateador. El único acceso privado queda
    // aislado aquí, por TextView; no sustituimos el formateador global de la aplicación.
    private static readonly FieldInfo FormatterField = typeof(TextView).GetField("_formatter", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new NotSupportedException("AvaloniaEdit ya no expone el campo _formatter; revisar la integración de bloques Markdown.");

    public static void Register(ITextRunConstructionContext context, LivePreviewState state)
    {
        // OnDocumentChanged restablece el formateador: se comprueba al generar cada línea para
        // cubrir también el cambio de nota/documento sin retener editores cerrados.
        if (FormatterField.GetValue(context.TextView) is TextFormatter formatter and not MarkdownBlockTextFormatter)
            FormatterField.SetValue(context.TextView, new MarkdownBlockTextFormatter(formatter));
        var line = context.VisualLine.FirstDocumentLine;
        var text = context.Document.GetText(line);
        var model = MarkdownLineParser.Parse(text, line.Offset, state.GetFence(line.LineNumber));
        if (IsJustifiable(state, line.LineNumber, text, model)) Justifiable.Add(context.VisualLine, model);
        if (model.Kind is MdBlockKind.BulletList or MdBlockKind.OrderedList or MdBlockKind.TaskList)
            Blocks.Add(context.VisualLine, new BlockLayout(model.ContentStart - line.Offset, 0));
        else if (model.Kind == MdBlockKind.Blockquote)
            Blocks.Add(context.VisualLine, new BlockLayout(model.ContentStart - line.Offset,
                MarkdownQuoteLayout.ContentIndent(model.QuoteDepth)));
    }

    /// <summary>Si la línea <paramref name="lineNumber"/> entra en el texto justificado. Fuera quedan las
    /// que no son prosa (una tabla, un bloque con alineación HTML propia, un encabezado), las que llevan
    /// tabuladores —cuyo avance lo decide el formateador y no se puede repartir sin descuadrar las
    /// paradas— y las que dibujan una imagen, cuyo ancho no es texto.
    ///
    /// La excepción de los cuatro espacios iniciales es solo para párrafos: en Markdown esa sangría
    /// significa "bloque de código" y su espaciado es literal. En una lista, en cambio, la sangría es lo
    /// que marca el nivel de anidamiento y no dice nada del contenido.</summary>
    private static bool IsJustifiable(LivePreviewState state, int lineNumber, string text, MdLine model)
    {
        if (!state.JustifyParagraphs || text.Length == 0 || text.Contains('\t')) return false;
        if (!MarkdownJustification.IsJustifiableBlock(model.Kind)) return false;
        if (state.GetAlignment(lineNumber) != MdAlign.None) return false;
        if (state.Tables.GetRow(lineNumber) is not null) return false;
        if (model.Spans.Exists(span => span.Kind == MdSpanKind.Image)) return false;

        if (model.Kind == MdBlockKind.Paragraph)
        {
            var indent = 0;
            while (indent < text.Length && text[indent] == ' ') indent++;
            if (indent >= 4) return false;
        }
        return true;
    }

    public override TextLine? FormatLine(ITextSource textSource, int firstTextSourceIndex, double paragraphWidth,
        TextParagraphProperties paragraphProperties, TextLineBreak? previousLineBreak = null) =>
        FormatLine(textSource, firstTextSourceIndex, paragraphWidth, paragraphProperties, previousLineBreak, null);

    public override TextLine? FormatLine(ITextSource textSource, int firstTextSourceIndex, double paragraphWidth,
        TextParagraphProperties paragraphProperties, TextLineBreak? previousLineBreak, TextRunCache? textRunCache)
    {
        if (textSource is not ITextRunConstructionContext context)
            return inner.FormatLine(textSource, firstTextSourceIndex, paragraphWidth, paragraphProperties, previousLineBreak, textRunCache);

        if (!Blocks.TryGetValue(context.VisualLine, out var layout))
        {
            var natural = inner.FormatLine(textSource, firstTextSourceIndex, paragraphWidth,
                paragraphProperties, previousLineBreak, textRunCache);
            if (natural is null || !Justifiable.TryGetValue(context.VisualLine, out var paragraph)) return natural;
            var justified = MarkdownJustification.Format(inner, natural, context, paragraph,
                paragraphWidth, paragraphProperties);
            return justified is null ? natural : new WrappedRow(justified, natural);
        }

        if (paragraphProperties.TextWrapping == TextWrapping.NoWrap || !double.IsFinite(paragraphWidth))
        {
            var unwrapped = inner.FormatLine(textSource, firstTextSourceIndex, paragraphWidth,
                paragraphProperties, previousLineBreak, textRunCache);
            return unwrapped is null || layout.FirstLineIndent <= 0
                ? unwrapped
                : Position(unwrapped, layout.FirstLineIndent, paragraphProperties);
        }

        var indent = firstTextSourceIndex == 0 ? layout.FirstLineIndent : layout.Indent;
        var contentWidth = Math.Max(1, paragraphWidth - indent);
        var line = inner.FormatLine(textSource, firstTextSourceIndex, contentWidth,
            paragraphProperties, previousLineBreak, textRunCache);
        if (line is null) return null;

        if (firstTextSourceIndex == 0)
        {
            var column = context.VisualLine.GetVisualColumn(layout.ContentOffset);
            // Con un editor más estrecho que el propio prefijo se conserva la primera fila y se
            // mide el prefijo completo aparte; no se limita la sangría a la mitad del editor.
            if (column < line.Length)
                layout.Indent = indent + line.GetDistanceFromCharacterHit(new CharacterHit(column, 0));
            else
            {
                using var prefix = inner.FormatLine(new PrefixSource(textSource, column), 0, double.PositiveInfinity,
                    new RowProperties(paragraphProperties, TextAlignment.Left));
                layout.Indent = indent + (prefix?.WidthIncludingTrailingWhitespace ?? 0);
            }
        }

        return Compose(line, context, indent, contentWidth, paragraphWidth, paragraphProperties);
    }

    /// <summary>Deja la fila de una lista o una cita en su sitio: justificada dentro de su ancho de
    /// contenido si toca, y en cualquier caso desplazada por su sangría.
    ///
    /// Una fila justificada mide exactamente el ancho de contenido, así que colocarla alineada a la
    /// derecha en una caja del ancho del párrafo la deja empezando justo en la sangría — un único formateo
    /// en vez de los dos que necesita una fila de ancho desconocido.</summary>
    private TextLine Compose(TextLine line, ITextRunConstructionContext context, double indent, double contentWidth,
        double paragraphWidth, TextParagraphProperties paragraphProperties)
    {
        if (Justifiable.TryGetValue(context.VisualLine, out var block) &&
            MarkdownJustification.CanJustifyRow(line, context, contentWidth, paragraphProperties) &&
            MarkdownJustification.BuildRuns(line, context, block, contentWidth) is { } runs)
        {
            // La alineación a la derecha de Avalonia cuadra el ancho *con* el blanco final de la fila —el
            // espacio en el que el formateador decidió partir—, así que la caja tiene que contarlo también:
            // sin eso la fila entraría justo ese espacio por delante de su sangría.
            var trailing = line.WidthIncludingTrailingWhitespace - line.Width;
            var justified = MarkdownJustification.Position(inner, runs, line.FirstTextSourceIndex,
                indent + contentWidth + trailing, paragraphProperties);
            if (justified is not null) return new WrappedRow(justified, line);
        }

        return indent <= 0 ? line : Position(line, indent, paragraphProperties);
    }

    private TextLine Position(TextLine line, double indent, TextParagraphProperties paragraphProperties)
    {
        // La segunda pasada no decide saltos: reproduce únicamente los runs de la fila ya partida.
        // Conservamos el TextLineBreak original para que la siguiente fila siga el mismo párrafo.
        var source = new RowSource(line);
        var properties = new RowProperties(paragraphProperties, TextAlignment.Right);
        using var cache = new TextRunCache();
        using var measured = inner.FormatLine(source, line.FirstTextSourceIndex, double.PositiveInfinity, properties, null, cache);
        var positioned = inner.FormatLine(source, line.FirstTextSourceIndex,
            indent + (measured?.WidthIncludingTrailingWhitespace ?? line.WidthIncludingTrailingWhitespace), properties, null, cache);
        return positioned is null ? line : new WrappedRow(positioned, line);
    }

    private sealed class PrefixSource(ITextSource source, int end) : ITextSource
    {
        public TextRun? GetTextRun(int textSourceIndex)
        {
            if (textSourceIndex >= end) return null;
            var run = source.GetTextRun(textSourceIndex);
            return run is TextCharacters characters && textSourceIndex + run.Length > end
                ? new TextCharacters(characters.Text[..(end - textSourceIndex)], characters.Properties!) : run;
        }
    }

    private sealed class RowSource(TextLine line) : ITextSource
    {
        public TextRun? GetTextRun(int textSourceIndex)
        {
            var offset = line.FirstTextSourceIndex;
            foreach (var run in line.TextRuns)
            {
                if (offset == textSourceIndex)
                    return run is ShapedTextRun ? new TextCharacters(run.Text, run.Properties!) : run;
                offset += run.Length;
            }
            return null;
        }
    }

    internal sealed class RowProperties(TextParagraphProperties source, TextAlignment alignment) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => source.FlowDirection;
        public override TextAlignment TextAlignment => alignment;
        public override double LineHeight => source.LineHeight;
        public override bool FirstLineInParagraph => source.FirstLineInParagraph;
        public override TextRunProperties DefaultTextRunProperties => source.DefaultTextRunProperties;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override double Indent => 0;
        public override double DefaultIncrementalTab => source.DefaultIncrementalTab;
        public override double LetterSpacing => source.LetterSpacing;
        public override TextDecorationCollection? TextDecorations => source.TextDecorations;
    }

    /// <summary>Delega toda la geometría en la fila nativa alineada y mantiene vivo el salto original.</summary>
    private sealed class WrappedRow(TextLine positioned, TextLine wrapped) : TextLine
    {
        public override IReadOnlyList<TextRun> TextRuns => positioned.TextRuns;
        public override int FirstTextSourceIndex => positioned.FirstTextSourceIndex;
        public override int Length => wrapped.Length;
        public override TextLineBreak? TextLineBreak => wrapped.TextLineBreak;
        public override double Baseline => positioned.Baseline;
        public override double Extent => positioned.Extent;
        public override bool HasCollapsed => positioned.HasCollapsed;
        public override bool HasOverflowed => wrapped.HasOverflowed;
        public override double Height => positioned.Height;
        public override int NewLineLength => wrapped.NewLineLength;
        public override double OverhangAfter => positioned.OverhangAfter;
        public override double OverhangLeading => positioned.OverhangLeading;
        public override double OverhangTrailing => positioned.OverhangTrailing;
        public override double Start => positioned.Start;
        public override int TrailingWhitespaceLength => positioned.TrailingWhitespaceLength;
        // AvaloniaEdit usa estos anchos como coordenada del borde derecho (scroll y espacio
        // virtual), porque su formateador habitual siempre devuelve Start = 0.
        public override double Width => positioned.Start + positioned.Width;
        public override double WidthIncludingTrailingWhitespace => positioned.Start + positioned.WidthIncludingTrailingWhitespace;
        public override void Draw(DrawingContext drawingContext, Point lineOrigin) => positioned.Draw(drawingContext, lineOrigin);
        public override TextLine Collapse(params TextCollapsingProperties?[] collapsingPropertiesList) => positioned.Collapse(collapsingPropertiesList);
        public override void Justify(JustificationProperties justificationProperties) => positioned.Justify(justificationProperties);
        public override CharacterHit GetCharacterHitFromDistance(double distance) => positioned.GetCharacterHitFromDistance(distance);
        public override double GetDistanceFromCharacterHit(CharacterHit characterHit) => positioned.GetDistanceFromCharacterHit(characterHit);
        public override CharacterHit GetNextCaretCharacterHit(CharacterHit characterHit) => positioned.GetNextCaretCharacterHit(characterHit);
        public override CharacterHit GetPreviousCaretCharacterHit(CharacterHit characterHit) => positioned.GetPreviousCaretCharacterHit(characterHit);
        public override CharacterHit GetBackspaceCaretCharacterHit(CharacterHit characterHit) => positioned.GetBackspaceCaretCharacterHit(characterHit);
        public override IReadOnlyList<TextBounds> GetTextBounds(int firstTextSourceCharacterIndex, int textLength) => positioned.GetTextBounds(firstTextSourceCharacterIndex, textLength);
        public override void Dispose()
        {
            positioned.Dispose();
            wrapped.Dispose();
        }
    }
}
