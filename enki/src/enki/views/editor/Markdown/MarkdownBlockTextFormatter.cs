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
    private static readonly ConditionalWeakTable<VisualLine, MdLine> Paragraphs = new();

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
        if (state.JustifyParagraphs && model.Kind == MdBlockKind.Paragraph &&
            state.GetAlignment(line.LineNumber) == MdAlign.None &&
            state.Tables.GetRow(line.LineNumber) is null &&
            text.Length > 0 && !char.IsWhiteSpace(text[0]) && !text.Contains('\t') &&
            !model.Spans.Exists(span => span.Kind == MdSpanKind.Image))
            Paragraphs.Add(context.VisualLine, model);
        if (model.Kind is MdBlockKind.BulletList or MdBlockKind.OrderedList or MdBlockKind.TaskList)
            Blocks.Add(context.VisualLine, new BlockLayout(model.ContentStart - line.Offset, 0));
        else if (model.Kind == MdBlockKind.Blockquote)
            Blocks.Add(context.VisualLine, new BlockLayout(model.ContentStart - line.Offset,
                MarkdownQuoteLayout.ContentIndent(model.QuoteDepth)));
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
            if (natural is null || !Paragraphs.TryGetValue(context.VisualLine, out var paragraph)) return natural;
            var justified = MarkdownParagraphJustification.Format(inner, natural, context, paragraph,
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
        var line = inner.FormatLine(textSource, firstTextSourceIndex, Math.Max(1, paragraphWidth - indent),
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
            return indent <= 0 ? line : Position(line, indent, paragraphProperties);
        }

        if (indent <= 0) return line;
        return Position(line, indent, paragraphProperties);
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
