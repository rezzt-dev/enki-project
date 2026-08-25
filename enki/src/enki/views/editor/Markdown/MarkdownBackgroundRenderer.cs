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

        // Fenced code blocks get one border around the whole block (like the preview's <pre>), not one
        // per line, so consecutive lines don't each draw their own edge and look like stacked boxes.
        double? codeBlockTop = null;
        double codeBlockBottom = 0;

        void FlushCodeBlockBorder()
        {
            if (codeBlockTop is not { } t) return;
            drawingContext.DrawRectangle(null, codeBorderPen, new Rect(0, t, width, codeBlockBottom - t));
            codeBlockTop = null;
        }

        foreach (var visualLine in textView.VisualLines)
        {
            var docLine = visualLine.FirstDocumentLine;
            var text = textView.Document.GetText(docLine);
            var fence = _state.GetFence(docLine.LineNumber);
            var model = MarkdownLineParser.Parse(text, docLine.Offset, fence);
            var top = visualLine.VisualTop - textView.VerticalOffset;

            if (model.Kind != MdBlockKind.CodeFence) FlushCodeBlockBorder();

            switch (model.Kind)
            {
                case MdBlockKind.CodeFence:
                    drawingContext.FillRectangle(_style.CodeBlockBackground, new Rect(0, top, width, visualLine.Height), 0f);
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

        FlushCodeBlockBorder();

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
