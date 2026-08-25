using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;

namespace enki.Views.Editor.Markdown;

/// <summary>Left-margin gutter that draws the blockquote rail outside the text flow, in its own
/// fixed-width column, instead of as reserved inline space before the quoted text.
///
/// Why a margin instead of reserving inline width on the "&gt; " marker (the approach
/// <see cref="MarkdownElementGenerator"/> still uses for bullets): AvaloniaEdit computes an
/// adaptive per-paragraph wrap indent (<c>TextEditorOptions.InheritWordWrapIndentation</c>) so a
/// long quote's continuation lines are supposed to land under its first word, same as the first
/// line - but Avalonia's own text formatter never reads
/// <c>Avalonia.Media.TextFormatting.TextParagraphProperties.Indent</c>, so that adaptive indent is
/// a silent no-op on this stack. Any inline reserved width therefore only ever affects the FIRST
/// visual row of a wrapped line; every continuation row renders flush left regardless, which made
/// the rail collide with text on every row after the first. A margin's width is constant for the
/// whole viewport - the same gap applies to a wrapped line's first row and every continuation row
/// alike, with zero dependency on text-flow indentation - so it's the only way to get Obsidian-style
/// consistent separation between the rail and the quoted text.
///
/// <see cref="GutterWidth"/> is sized to exactly replace the left padding EditorView.axaml trims off
/// the &lt;ae:TextEditor&gt; (20px there down to 4px) rather than adding on top of it, so heading/
/// list/plain-paragraph text lands at the same X it always did - only the blockquote rail moves into
/// what used to be blank padding. Changing this constant without adjusting that XAML padding by the
/// same amount will shift all text sideways again.
///
/// The rail sits near the RIGHT edge of that gutter (<see cref="GapToText"/> from where text starts),
/// not centered or flush left in it: a fenced code block's background/border spans the full text
/// width starting at that same right edge, so a rail floating further left, with a wide gap of empty
/// gutter behind it, reads as disconnected from the content column those blocks establish.</summary>
public sealed class QuoteRailMargin : AbstractMargin
{
    private const double GutterWidth = 16;
    private const double RailThickness = 3;
    private const double GapToText = 4;
    private const double RailOffset = GutterWidth - RailThickness - GapToText;

    private readonly LivePreviewState _state;
    private readonly MarkdownStyle _style;

    public QuoteRailMargin(LivePreviewState state, MarkdownStyle style)
    {
        _state = state;
        _style = style;
    }

    protected override Size MeasureOverride(Size availableSize) => new(GutterWidth, 0);

    public override void Render(DrawingContext context)
    {
        var textView = TextView;
        if (textView?.Document is null) return;

        foreach (var visualLine in textView.VisualLines)
        {
            var docLine = visualLine.FirstDocumentLine;
            var text = textView.Document.GetText(docLine);
            var fence = _state.GetFence(docLine.LineNumber);
            var model = MarkdownLineParser.Parse(text, docLine.Offset, fence);
            if (model.Kind != MdBlockKind.Blockquote) continue;

            var top = visualLine.VisualTop - textView.VerticalOffset;
            context.FillRectangle(_style.QuoteBar,
                new Rect(RailOffset, top + 2, RailThickness, visualLine.Height - 4), 2f);
        }
    }
}
