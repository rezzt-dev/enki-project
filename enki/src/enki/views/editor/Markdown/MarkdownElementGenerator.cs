using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Conceals Markdown syntax markers (headings, emphasis, links, blockquote/list markers) and the
/// recognized HTML tags (<c>&lt;div align="center"&gt;</c>, <c>&lt;b&gt;</c>…) wherever the
/// caret/selection isn't touching them, replaces list bullets and task checkboxes with rendered
/// widgets, and reserves the leading space that centres or right-aligns a line - the core mechanic
/// behind Obsidian's live preview.
/// </summary>
public sealed class MarkdownElementGenerator : VisualLineElementGenerator
{
    /// <summary>Checkbox box size and the gap after it, mirrored by the measurement that aligns a task
    /// line (see <see cref="CreateInertCheckbox"/>).</summary>
    private const double CheckboxSize = 16;
    private const double CheckboxSpacing = 6;

    private enum ZoneKind { Hide, Bullet, Checkbox, Align, JustifySpace }

    /// <param name="Spacer">Extra blank width (px) this zone reserves before whatever it renders: the
    /// alignment spacing of the visual row starting here, or the share of leftover width a justified
    /// row hands to this space character. <c>NaN</c> means "no row plan yet, estimate it from the whole
    /// line" (see <see cref="ComputeSpacerWidth"/>), and 0 means none.
    ///
    /// The spacing rides on whatever element comes first at that offset instead of being a zone of its
    /// own: element construction asks each generator only once per offset, so a separate zero-length zone
    /// sharing another zone's offset would make that other one be skipped.</param>
    private readonly record struct Zone(int Start, int End, ZoneKind Kind, MdLine Line, double Spacer);

    private readonly LivePreviewState _state;
    private readonly TextArea _textArea;
    private readonly MarkdownStyle _style;
    private readonly MarkdownAlignmentLayout _layout = new();

    private int _cachedLineNumber = -1;
    private List<Zone> _cachedZones = new();

    public MarkdownElementGenerator(LivePreviewState state, TextArea textArea, MarkdownStyle style)
    {
        _state = state;
        _textArea = textArea;
        _style = style;

        // An aligned line's spacing is measured against the text column's current width, so a resize
        // invalidates every plan. AvaloniaEdit only rebuilds visual lines on resize when word wrap is on,
        // hence the explicit redraw - skipped entirely for the ordinary note, which declares no alignment.
        textArea.TextView.SizeChanged += (_, _) =>
        {
            if (!_state.HasAlignments) return;
            _layout.Invalidate();
            textArea.TextView.Redraw();
        };

        // Each layout pass hands back the rows the formatter actually produced, which is the only place
        // the per-row spacing can be worked out from - see MarkdownAlignmentLayout.
        textArea.TextView.VisualLinesChanged += (_, _) =>
        {
            if (_state.HasAlignments) _layout.Refresh(textArea.TextView, _state);
        };
    }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _cachedLineNumber = -1;
    }

    private List<Zone> GetZones(int lineNumber)
    {
        if (_cachedLineNumber == lineNumber) return _cachedZones;

        var line = CurrentContext.Document.GetLineByNumber(lineNumber);
        var text = CurrentContext.Document.GetText(line);
        var fence = _state.GetFence(lineNumber);
        var model = MarkdownLineParser.Parse(text, line.Offset, fence);
        var zones = new List<Zone>();
        var lineActive = _state.Intersects(line.Offset, line.EndOffset);

        switch (model.Kind)
        {
            case MdBlockKind.Heading:
            case MdBlockKind.HorizontalRule:
            // Code fences need no zone of their own beyond concealing the "```lang" delimiter: the
            // padding that keeps code text clear of the block's border is produced by drawing the box
            // wider than the text column (see MarkdownBackgroundRenderer), not by inserting a spacer
            // run here. A spacer only ever indents the first visual row of a wrapped line, which is
            // precisely why the box used to be overrun by every continuation row.
            case MdBlockKind.CodeFence:
                if (!lineActive && model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Hide, model, 0));
                break;
            case MdBlockKind.Blockquote:
                // No reserved-width concern here: the quote rail lives in QuoteRailMargin's own
                // gutter column now, not inline before the text, so the '>' marker can collapse to
                // zero width exactly like the other Hide zones below.
                if (model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Hide, model, 0));
                break;
            case MdBlockKind.BulletList:
                if (model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Bullet, model, 0));
                break;
            case MdBlockKind.TaskList:
                if (model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Checkbox, model, 0));
                break;
        }

        foreach (var span in model.Spans)
        {
            if (_state.Intersects(span.Start, span.End)) continue;
            if (span.ContentStart > span.Start) zones.Add(new Zone(span.Start, span.ContentStart, ZoneKind.Hide, model, 0));
            if (span.End > span.ContentEnd) zones.Add(new Zone(span.ContentEnd, span.End, ZoneKind.Hide, model, 0));
        }

        zones.Sort((a, b) => a.Start.CompareTo(b.Start));

        AddAlignmentZones(zones, line, text, model);

        _cachedLineNumber = lineNumber;
        _cachedZones = zones;
        return zones;
    }

    /// <summary>Injects the blank space that aligns the line: one spacer at the start of each visual row
    /// (centre / right), or a share of the row's leftover width on each of its spaces (justify). The rows
    /// come from <see cref="MarkdownAlignmentLayout"/>, which reads them back from the previous layout
    /// pass; until it has seen the line once, a single-row estimate stands in so a short centred line is
    /// never left flush left for a frame.</summary>
    private void AddAlignmentZones(List<Zone> zones, DocumentLine line, string text, MdLine model)
    {
        var align = _state.GetAlignment(line.LineNumber);
        if (align is MdAlign.None or MdAlign.Left) return;

        var plan = _layout.Get(line.LineNumber);
        if (plan is null || plan.Align != align || plan.Rows.Count == 0)
        {
            if (align is MdAlign.Center or MdAlign.Right) AddSpacerZone(zones, line, model, line.Offset, double.NaN);
            return;
        }

        for (var i = 0; i < plan.Rows.Count; i++)
        {
            var row = plan.Rows[i];
            row.Applied = 0;

            if (row.Leading > 0.5)
            {
                // A row that does not start after a space (a word split mid-way because it is longer than
                // the column) is left alone: the spacer glues itself to the following text to stay on its
                // own row, and gluing it to half a word would drag that half up to the row before.
                if (i == 0 || StartsAfterSpace(text, line.Offset, row.StartOffset))
                {
                    if (AddSpacerZone(zones, line, model, row.StartOffset, row.Leading)) row.Applied += row.Leading;
                }
            }

            if (row.Free > 0.5) row.Applied += AddJustifyZones(zones, line, text, model, row);
        }

        zones.Sort(CompareZones);
    }

    /// <summary>Shares the row's leftover width out between the spaces it can stretch, and returns how
    /// much was actually injected. Spaces sitting inside a concealed marker are skipped (stretching them
    /// would open a gap where nothing is drawn), and so is the row's trailing space, which a justified
    /// browser line does not stretch either.</summary>
    private static double AddJustifyZones(List<Zone> zones, DocumentLine line, string text, MdLine model, AlignedRow row)
    {
        var stretchable = new List<int>();
        var lastVisible = -1;
        for (var offset = row.StartOffset; offset < row.EndOffset; offset++)
        {
            var index = offset - line.Offset;
            if (index < 0 || index >= text.Length) break;
            if (IsInsideZone(zones, offset)) continue;
            if (text[index] == ' ') stretchable.Add(offset);
            else lastVisible = offset;
        }

        // Drop the trailing run of spaces - anything past the last visible character of the row.
        while (stretchable.Count > 0 && stretchable[^1] > lastVisible) stretchable.RemoveAt(stretchable.Count - 1);
        if (stretchable.Count == 0) return 0;

        // Half a pixel short of the full leftover, so rounding can never push the row past the column
        // width and make the formatter break it one word earlier than it just did.
        var extra = (row.Free - 0.5) / stretchable.Count;
        if (extra <= 0.1) return 0;

        foreach (var offset in stretchable)
            zones.Add(new Zone(offset, offset + 1, ZoneKind.JustifySpace, model, extra));

        return extra * stretchable.Count;
    }

    /// <summary>Puts <paramref name="width"/> of blank space at <paramref name="offset"/>, riding on the
    /// zone already starting there if there is one. Returns whether the spacing was placed.</summary>
    private static bool AddSpacerZone(List<Zone> zones, DocumentLine line, MdLine model, int offset, double width)
    {
        for (var i = 0; i < zones.Count; i++)
        {
            if (zones[i].Start != offset) continue;
            if (zones[i].Kind == ZoneKind.Align || zones[i].Spacer != 0) return false;
            zones[i] = zones[i] with { Spacer = width };
            return true;
        }

        zones.Add(new Zone(offset, offset, ZoneKind.Align, model, width));
        return true;
    }

    private static bool StartsAfterSpace(string text, int lineOffset, int offset)
    {
        var index = offset - lineOffset - 1;
        return index >= 0 && index < text.Length && (text[index] == ' ' || text[index] == '\t');
    }

    private static bool IsInsideZone(List<Zone> zones, int offset)
    {
        foreach (var zone in zones)
        {
            if (zone.Start <= offset && zone.End > offset) return true;
        }
        return false;
    }

    /// <summary>Zero-length alignment zones sort before anything else starting at the same offset, so the
    /// row's spacing is never hidden behind a marker that begins there.</summary>
    private static int CompareZones(Zone a, Zone b)
    {
        if (a.Start != b.Start) return a.Start.CompareTo(b.Start);
        if (a.Kind == b.Kind) return 0;
        return a.Kind == ZoneKind.Align ? -1 : b.Kind == ZoneKind.Align ? 1 : 0;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var line = CurrentContext.Document.GetLineByOffset(startOffset);
        foreach (var zone in GetZones(line.LineNumber))
        {
            if (zone.Start >= startOffset) return zone.Start;
        }
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.Document.GetLineByOffset(offset);
        var zones = GetZones(line.LineNumber);
        foreach (var zone in zones)
        {
            if (zone.Start != offset) continue;
            var length = zone.End - zone.Start;
            var spacer = double.IsNaN(zone.Spacer) ? ComputeSpacerWidth(line, zone.Line, zones) : zone.Spacer;
            VisualLineElement? element = zone.Kind switch
            {
                // Single trailing space matches the gap ordered lists get naturally from "1. ".
                ZoneKind.Bullet => new WhitespaceFormattedElement("• ", length, spacer),
                ZoneKind.Checkbox => new InlineObjectElement(length, BuildCheckbox(zone.Line, spacer)),
                ZoneKind.Align => spacer > 0 ? new WhitespaceFormattedElement(string.Empty, 0, spacer) : null,
                // The space keeps its own glyph and grows by its share of the row's leftover width.
                ZoneKind.JustifySpace => new WhitespaceFormattedElement(" ", length, spacer),
                _ => spacer > 0
                    ? new WhitespaceFormattedElement(string.Empty, length, spacer)
                    : new FormattedTextElement(string.Empty, length)
            };
            return element;
        }
        return null;
    }

    /// <summary>Blank space to reserve before the line so its visible text ends up centred or pushed to
    /// the right edge of the text column. AvaloniaEdit gives every visual line the same paragraph
    /// properties, with <c>TextAlignment</c> fixed to <c>Left</c>, so moving the text is the only way to
    /// align it - and moving it means measuring it first (<see cref="MarkdownLineMeasurer"/>).</summary>
    private double ComputeSpacerWidth(DocumentLine line, MdLine model, List<Zone> zones)
    {
        var align = _state.GetAlignment(line.LineNumber);
        if (align is not (MdAlign.Center or MdAlign.Right)) return 0;

        var available = CurrentContext.TextView.Bounds.Width;
        if (double.IsNaN(available) || available <= 0) return 0;

        var hidden = new List<(int Start, int End)>(zones.Count);
        double replacements = 0;
        foreach (var zone in zones)
        {
            if (zone.Kind == ZoneKind.Align) continue;
            hidden.Add((zone.Start, zone.End));
            replacements += zone.Kind switch
            {
                ZoneKind.Bullet => MarkdownLineMeasurer.MeasureGlyph("• ", model, _style),
                ZoneKind.Checkbox => CheckboxSize + CheckboxSpacing,
                _ => 0
            };
        }

        var styles = MarkdownHtmlParser.BuildStyleRanges(
            model.HtmlTags, line.Offset, line.EndOffset, _state.GetHtml(line.LineNumber).OpenInlineTags);
        var content = replacements + MarkdownLineMeasurer.MeasureVisible(
            CurrentContext.Document.GetText(line), line.Offset, model, hidden, styles, _state.Intersects, _style);

        // A line with nothing visible on it (the "<div align=\"center\">" line itself) has nothing to
        // centre: reserving half the column there would only strand the caret in the middle of a blank line.
        if (content <= 0) return 0;

        // A line too long for the column keeps its left edge: the reserved space only ever moves the
        // first visual row, so pushing it across would leave the continuation rows behind it (the same
        // limitation the wrap indentation runs into - see WhitespaceFormattedElement).
        var free = available - content;
        if (free <= 1) return 0;
        return align == MdAlign.Center ? free / 2 : free;
    }

    private CheckBox BuildCheckbox(MdLine model, double leadingWidth)
    {
        var checkBox = CreateInertCheckbox(model.TaskChecked, _textArea, leadingWidth);

        if (model.CheckboxCharOffset >= 0)
        {
            // Anchored rather than a plain captured int: the visual line (and this widget) can outlive a
            // document edit made elsewhere before AvaloniaEdit gets around to rebuilding it, and a raw
            // offset would then point at the wrong character on click, corrupting whatever text had shifted
            // into its place. TextAnchor tracks the "[ ]" char through any such edit automatically.
            var anchor = _textArea.Document.CreateAnchor(model.CheckboxCharOffset);
            anchor.MovementType = AnchorMovementType.BeforeInsertion;

            checkBox.IsHitTestVisible = true;
            checkBox.Click += (_, _) =>
            {
                if (anchor.IsDeleted) return;
                var doc = _textArea.Document;
                var offset = anchor.Offset;
                if (offset < 0 || offset >= doc.TextLength) return;
                var current = doc.GetCharAt(offset);
                var next = current is 'x' or 'X' ? ' ' : 'x';
                doc.Replace(offset, 1, next.ToString());
            };
        }

        return checkBox;
    }

    /// <summary>The 16px box (see CheckBox.markdown-checkbox in Styles.axaml) sits inside an
    /// AvaloniaEdit <see cref="InlineObjectElement"/>, whose TextView.ArrangeOverride places the
    /// control at the top of the line box. To center the box with the caret — which spans the full
    /// line height — we push it down by half the difference between the editor line height and the
    /// box, and pin the inline run's baseline to the text baseline so the surrounding line height
    /// does not grow.</summary>
    private static CheckBox CreateInertCheckbox(bool isChecked, TextArea textArea, double leadingWidth)
    {
        const double boxSize = CheckboxSize;
        var textView = textArea.TextView;
        var topMargin = Math.Max(0, (textView.DefaultLineHeight - boxSize) / 2);

        var checkBox = new CheckBox
        {
            IsChecked = isChecked,
            IsHitTestVisible = false,
            Padding = default,
            Width = boxSize,
            Height = boxSize,
            MinWidth = boxSize,
            MinHeight = boxSize,
            Margin = new Avalonia.Thickness(leadingWidth, topMargin, CheckboxSpacing, 0),
            VerticalAlignment = VerticalAlignment.Top,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        checkBox.Classes.Add("markdown-checkbox");
        TextBlock.SetBaselineOffset(checkBox, textView.DefaultBaseline);
        return checkBox;
    }
}
