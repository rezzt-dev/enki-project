using System;
using System.Collections.Generic;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace enki.Views.Editor.Markdown;

/// <summary>One visual row of an aligned line: where it starts and ends in the document, and how much
/// blank space the renderer has to inject to align it.</summary>
internal sealed class AlignedRow
{
    public int StartOffset { get; init; }
    public int EndOffset { get; init; }
    public bool IsLast { get; init; }

    /// <summary>Blank space reserved before the row (centre / right).</summary>
    public double Leading { get; init; }

    /// <summary>Width left over on the row, to be shared out between its spaces (justify). Zero on the
    /// last row of a paragraph — a browser does not stretch it either.</summary>
    public double Free { get; init; }

    /// <summary>What the generator actually injected into this row, so the next measurement can subtract
    /// it and recover the row's own content width instead of feeding its own spacing back in.</summary>
    public double Applied { get; set; }
}

internal sealed class AlignedLine
{
    public MdAlign Align { get; init; }
    public double Available { get; init; }
    public List<AlignedRow> Rows { get; init; } = new();

    /// <summary>How many times in a row this plan has been rewritten. Bounded so a line whose width
    /// measurement never settles (a pathological mix of fonts and wrap points) can't ping-pong redraws
    /// forever — see <see cref="MarkdownAlignmentLayout"/>.</summary>
    public int Revisions { get; set; }

    public AlignedRow? RowAt(int startOffset)
    {
        foreach (var row in Rows)
        {
            if (row.StartOffset == startOffset) return row;
        }
        return null;
    }
}

/// <summary>
/// Works out, per visual row, the blank space that centres / right-aligns / justifies an HTML-aligned
/// line — the part <see cref="MarkdownElementGenerator"/> cannot answer on its own.
///
/// AvaloniaEdit formats every visual line with the same paragraph properties, whose
/// <c>TextAlignment</c> is hardcoded to <c>Left</c>, so alignment has to be built out of the only thing a
/// generator controls: element widths. That in turn needs to know where the formatter broke the line into
/// rows and how wide each row's text came out — which is only knowable *after* a layout pass. So this
/// class reads the finished <see cref="VisualLine.TextLines"/> after every layout, turns them into a plan,
/// and asks for one more redraw; the next pass renders the line aligned.
///
/// The plan is stable, so this settles after that single extra pass instead of looping: a row's own
/// spacing can never change where the formatter breaks it.
/// <list type="bullet">
/// <item>Right and justify fill the row to exactly the column width, so the next word still doesn't fit.</item>
/// <item>Centre adds half the row's leftover width. The formatter broke the row precisely because the
/// next word was wider than *all* the leftover, so it cannot fit in half of it either.</item>
/// </list>
/// </summary>
internal sealed class MarkdownAlignmentLayout
{
    /// <summary>Redraws one line's plan may trigger before it is left alone until the next document edit
    /// or resize. A plan that behaves needs exactly one.</summary>
    private const int MaxRevisions = 4;

    private static readonly TimeSpan RevisionWindow = TimeSpan.FromMilliseconds(400);

    private readonly Dictionary<int, AlignedLine> _lines = new();
    private DateTime _lastRevision = DateTime.MinValue;
    private bool _redrawQueued;

    public AlignedLine? Get(int lineNumber) => _lines.TryGetValue(lineNumber, out var line) ? line : null;

    /// <summary>Drops the measured widths — called when the text column is resized, since every row's
    /// leftover width is relative to it.</summary>
    public void Invalidate()
    {
        _lines.Clear();
        _lastRevision = DateTime.MinValue;
    }

    /// <summary>Re-reads the rows the formatter just produced for every visible aligned line, and asks for
    /// a redraw if any plan changed. Runs on <c>TextView.VisualLinesChanged</c>.</summary>
    public void Refresh(TextView textView, LivePreviewState state)
    {
        if (!textView.VisualLinesValid) return;

        var available = textView.Bounds.Width;
        if (double.IsNaN(available) || available <= 0) return;

        var changed = false;

        foreach (var visualLine in textView.VisualLines)
        {
            var docLine = visualLine.FirstDocumentLine;
            var lineNumber = docLine.LineNumber;
            var align = state.GetAlignment(lineNumber);

            if (align is MdAlign.None or MdAlign.Left)
            {
                changed |= _lines.Remove(lineNumber);
                continue;
            }

            var existing = Get(lineNumber);
            var widthChanged = existing is not null && Math.Abs(existing.Available - available) > 0.5;
            var rows = BuildRows(visualLine, docLine, align, available, widthChanged ? null : existing);

            if (existing is not null && !widthChanged && existing.Align == align && SameRows(existing.Rows, rows))
                continue;

            var revisions = existing is null || widthChanged || DateTime.UtcNow - _lastRevision > RevisionWindow
                ? 1
                : existing.Revisions + 1;

            _lines[lineNumber] = new AlignedLine
            {
                Align = align,
                Available = available,
                Rows = rows,
                Revisions = revisions
            };
            _lastRevision = DateTime.UtcNow;

            // Past the cap the plan is still kept up to date — only the extra redraw it would ask for is
            // dropped, so a line that refuses to settle just renders one pass behind instead of spinning.
            if (revisions <= MaxRevisions) changed = true;
        }

        if (changed) QueueRedraw(textView);
    }

    private static List<AlignedRow> BuildRows(
        VisualLine visualLine, DocumentLine docLine, MdAlign align, double available, AlignedLine? previous)
    {
        var rows = new List<AlignedRow>();
        var textLines = visualLine.TextLines;
        if (textLines is null || textLines.Count == 0) return rows;

        var visualColumn = 0;
        for (var i = 0; i < textLines.Count; i++)
        {
            var textLine = textLines[i];
            var startColumn = Math.Min(visualColumn, visualLine.VisualLength);
            visualColumn += textLine.Length;
            var endColumn = Math.Min(visualColumn, visualLine.VisualLength);

            var startOffset = docLine.Offset + visualLine.GetRelativeOffset(startColumn);
            var endOffset = docLine.Offset + visualLine.GetRelativeOffset(endColumn);

            // TextLine.Width already leaves out the row's trailing whitespace, which is what a browser
            // ignores when it centres a line too. What it does include is whatever spacing the previous
            // pass injected, so that comes back off here.
            var applied = previous?.RowAt(startOffset)?.Applied ?? 0;
            var content = Math.Max(0, textLine.Width - applied);
            var free = available - content;
            var isLast = i == textLines.Count - 1;

            rows.Add(new AlignedRow
            {
                StartOffset = startOffset,
                EndOffset = endOffset,
                IsLast = isLast,
                Leading = free > 1 && align is MdAlign.Center or MdAlign.Right
                    ? align == MdAlign.Center ? free / 2 : free
                    : 0,
                // A single-row line is also a "last row": justify leaves it alone, exactly like a browser
                // leaves the last line of a justified paragraph ranged left.
                Free = free > 1 && align == MdAlign.Justify && !isLast ? free : 0
            });
        }

        return rows;
    }

    private static bool SameRows(List<AlignedRow> a, List<AlignedRow> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].StartOffset != b[i].StartOffset || a[i].EndOffset != b[i].EndOffset) return false;
            if (Math.Abs(a[i].Leading - b[i].Leading) > 0.5) return false;
            if (Math.Abs(a[i].Free - b[i].Free) > 0.5) return false;
        }
        return true;
    }

    /// <summary>Posted rather than called inline: this runs from inside the layout pass that just produced
    /// the visual lines, and <c>Redraw</c> invalidates them again.</summary>
    private void QueueRedraw(TextView textView)
    {
        if (_redrawQueued) return;
        _redrawQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _redrawQueued = false;
            textView.Redraw();
        }, DispatcherPriority.Background);
    }
}
