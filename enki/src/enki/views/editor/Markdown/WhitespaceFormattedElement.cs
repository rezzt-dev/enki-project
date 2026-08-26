using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// A <see cref="VisualLineElement"/> that renders a replacement glyph (the "• " bullet) for a hidden
/// Markdown marker, reserving real visual width instead of collapsing to nothing — the built-in
/// <see cref="FormattedTextElement"/> always reports a
/// <see cref="VisualLineElement.VisualLength"/> of 1 regardless of its replacement string, which
/// isn't enough room for a multi-character glyph like "• ".
///
/// It doubles as the alignment spacer: a leading width can be reserved before the glyph (or with no
/// glyph at all, for an aligned line whose first element is a concealed tag or nothing in particular),
/// which is how a centred or right-aligned line gets pushed across — see
/// <see cref="MarkdownLineMeasurer"/> for why moving the text is the only way to align it.
///
/// <see cref="CreateTextRun"/> can be asked to (re)start partway through this element's reserved
/// span — not just once at its own <see cref="VisualLineElement.VisualColumn"/> — e.g. when
/// <c>GetIndentationVisualColumn</c>'s wrap-indent measurement probes each remaining visual column
/// individually. Ignoring <c>startVisualColumn</c> and always handing back the full glyph string
/// used to redraw "• " once per reserved column instead of once per element — a 2-column-wide
/// bullet rendered as two overlapping "• " glyphs side by side ("• • "). Slicing the string
/// to what's left from <c>startVisualColumn</c> keeps every probe correctly sized without
/// re-emitting the glyph itself past its first column.
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
    private readonly double _leadingWidth;
    private readonly Dictionary<int, FormattedText> _formattedByOffset = new();

    /// <param name="leadingWidth">Blank space (px) reserved before the glyph, used to align the line.</param>
    public WhitespaceFormattedElement(string text, int documentLength, double leadingWidth = 0)
        : base(Math.Max(1, text.Length), documentLength)
    {
        _text = text;
        _leadingWidth = leadingWidth;
    }

    public override bool IsWhitespace(int visualColumn) => true;

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var relativeOffset = Math.Clamp(startVisualColumn - VisualColumn, 0, _text.Length);
        var remaining = _text[relativeOffset..];
        // A run must carry at least one character (Avalonia's formatter maps character indices to pixel
        // positions through TextRun.Length), so a glyph-less spacer still hands back a single character —
        // drawn as nothing, and sized as the reserved width alone.
        //
        // That character is a non-breaking space on purpose. An alignment spacer sits at the start of a
        // visual row, and a plain space there would be a break opportunity: the formatter would leave the
        // spacer at the end of the row above and start the row flush left, undoing the alignment. A
        // no-break space glues it to the text it precedes, while still allowing the break in front of it
        // (the row above ends in a space).
        var hasGlyph = remaining.Length > 0;
        var glyph = hasGlyph ? remaining : "\u00A0";
        // Only the element's own first visual column carries the leading space; a probe that restarts
        // partway through the glyph must not re-reserve it.
        var leading = relativeOffset == 0 ? _leadingWidth : 0;

        if (!_formattedByOffset.TryGetValue(relativeOffset, out var formatted))
        {
            var rp = TextRunProperties;
            formatted = new FormattedText(
                glyph, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, rp.Typeface,
                rp.FontRenderingEmSize, rp.ForegroundBrush);
            _formattedByOffset[relativeOffset] = formatted;
        }
        return new SpacerTextRun(glyph, formatted, TextRunProperties, leading, hasGlyph);
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
        private readonly double _leading;
        private readonly bool _drawGlyph;
        public SpacerTextRun(string text, FormattedText ft, TextRunProperties props, double leading, bool drawGlyph)
        {
            _text = text;
            _ft = ft;
            _props = props;
            _leading = leading;
            _drawGlyph = drawGlyph;
        }
        public override ReadOnlyMemory<char> Text => _text.AsMemory();
        public override TextRunProperties? Properties => _props;
        public override double Baseline => _ft.Baseline;
        public override Size Size => new(_leading + (_drawGlyph ? _ft.WidthIncludingTrailingWhitespace : 0), _ft.Height);
        public override void Draw(DrawingContext context, Point origin)
        {
            if (_drawGlyph) context.DrawText(_ft, origin.WithX(origin.X + _leading));
        }
    }
}