using Avalonia;
using Avalonia.Media;
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
                case MdBlockKind.Blockquote:
                    drawingContext.FillRectangle(_style.QuoteBar, new Rect(2, top + 2, 3, visualLine.Height - 4), 2f);
                    break;
                case MdBlockKind.HorizontalRule when !_state.Intersects(docLine.Offset, docLine.EndOffset):
                {
                    var y = top + visualLine.Height / 2;
                    drawingContext.DrawLine(rulePen, new Point(0, y), new Point(width, y));
                    break;
                }
                case MdBlockKind.Table when model.IsTableSeparator && !_state.Intersects(docLine.Offset, docLine.EndOffset):
                {
                    var y = top + visualLine.Height / 2;
                    drawingContext.DrawLine(rulePen, new Point(0, y), new Point(width, y));
                    break;
                }
            }
        }

        FlushCodeBlockBorder();
    }
}
