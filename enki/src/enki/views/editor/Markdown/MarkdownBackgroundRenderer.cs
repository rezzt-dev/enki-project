using System;
using Avalonia;
using Avalonia.Media;
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
        // AvaloniaEdit computes for them (see QuoteRailMargin's doc comment for the full story). Any box
        // inset into [0, width] would therefore be overrun on both edges by every wrapped code line,
        // which is exactly the bug this replaced: an inset left edge with an inline spacer padding only
        // the FIRST row, and a right edge sitting exactly on the wrap boundary.
        //
        // Painting past TextView's own bounds needs its clipping relaxed by the same padding - see
        // EditorView.axaml.cs's UpdateCodeBlockBleed(), which also documents where that space comes from
        // (QuoteRailMargin's gutter on the left, TextEditor.Padding on the right). If a future Avalonia
        // version clips it anyway the block degrades to a full-width shaded band rather than breaking.
        var pad = MarkdownStyle.CodeBlockHorizontalPadding;
        var codeBoxLeft = -pad;
        var codeBoxWidth = width + pad * 2;
        var radius = MarkdownStyle.CodeBlockCornerRadius;

        // Fenced code blocks are painted as ONE rounded rect around the whole block (like the preview's
        // <pre>), not per line: per-line fills leave hairline seams between visual lines and per-line
        // borders read as a stack of separate boxes. Fill and border are both deferred to the flush so
        // the rounded corners only appear at the block's real top and bottom.
        double? codeBlockTop = null;
        double codeBlockBottom = 0;

        void FlushCodeBlock()
        {
            if (codeBlockTop is not { } t) return;
            var rect = new Rect(codeBoxLeft, t, codeBoxWidth, codeBlockBottom - t);
            drawingContext.DrawRectangle(_style.CodeBlockBackground, codeBorderPen, rect, radius, radius);
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
                // The blockquote rail is drawn by QuoteRailMargin instead, in its own left-gutter
                // column - see that class's doc comment for why it can't live here as inline
                // background painting the way the code-fence/rule decorations above do.
                case MdBlockKind.HorizontalRule when !_state.Intersects(docLine.Offset, docLine.EndOffset):
                {
                    var y = top + visualLine.Height / 2;
                    drawingContext.DrawLine(rulePen, new Point(0, y), new Point(width, y));
                    break;
                }

            }
        }

        FlushCodeBlock();

        DrawSearchHighlights(textView, drawingContext);
    }

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
