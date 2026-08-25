using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// A <see cref="VisualLineElement"/> that renders a replacement glyph (the "•  " bullet, currently
/// its only user) for a hidden Markdown marker, reserving real visual width instead of collapsing to
/// nothing — the built-in <see cref="FormattedTextElement"/> always reports a
/// <see cref="VisualLineElement.VisualLength"/> of 1 regardless of its replacement string, which
/// isn't enough room for a multi-character glyph like "•  ".
///
/// Note: this reserved width only ever affects the FIRST visual row of a wrapped line. AvaloniaEdit
/// computes an adaptive per-paragraph wrap indent (<c>TextEditorOptions.InheritWordWrapIndentation</c>)
/// meant to keep continuation rows aligned under it, but Avalonia's own text formatter never reads
/// <c>Avalonia.Media.TextFormatting.TextParagraphProperties.Indent</c> — that option is a silent
/// no-op on this stack, so a long wrapped bullet's continuation rows render flush left regardless
/// of what's reserved here. The blockquote rail sidesteps this entirely by living in
/// <see cref="QuoteRailMargin"/>'s own gutter column instead of reserving inline width at all - see
/// that class's doc comment for the full explanation.
/// </summary>
internal sealed class WhitespaceFormattedElement : VisualLineElement
{
    private readonly string _text;
    private FormattedText? _formatted;

    public WhitespaceFormattedElement(string text, int documentLength)
        : base(text.Length, documentLength)
    {
        _text = text;
    }

    public override bool IsWhitespace(int visualColumn) => true;

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        if (_formatted is null)
        {
            var rp = TextRunProperties;
            var typeface = rp.Typeface;
            _formatted = new FormattedText(
                _text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface,
                rp.FontRenderingEmSize, rp.ForegroundBrush);
        }
        return new SpacerTextRun(_text, _formatted, TextRunProperties);
    }

    public override bool CanSplit => false;

    /// <summary>
    /// A <see cref="DrawableTextRun"/> whose <see cref="Text"/> returns the actual
    /// spacer string. This is essential: Avalonia's text formatter uses
    /// <c>TextRun.Length</c> (=&gt; <c>Text.Length</c>) to map character indices to
    /// pixel positions via <c>TextLine.GetDistanceFromCharacterHit</c>. If
    /// <see cref="Text"/> returned empty, the run would contribute 0 characters
    /// and <c>GetIndentationVisualColumn</c>'s measurement would fall through to
    /// the content text instead of measuring the spacer width.
    /// </summary>
    private sealed class SpacerTextRun : DrawableTextRun
    {
        private readonly string _text;
        private readonly FormattedText _ft;
        private readonly TextRunProperties _props;
        public SpacerTextRun(string text, FormattedText ft, TextRunProperties props)
        {
            _text = text;
            _ft = ft;
            _props = props;
        }
        public override ReadOnlyMemory<char> Text => _text.AsMemory();
        public override TextRunProperties? Properties => _props;
        public override double Baseline => _ft.Baseline;
        public override Size Size => new(_ft.WidthIncludingTrailingWhitespace, _ft.Height);
        public override void Draw(DrawingContext context, Point origin)
            => context.DrawText(_ft, origin);
    }
}