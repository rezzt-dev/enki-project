using System.Collections.Generic;
using System.Linq;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Shared state consumed by the colorizer, element generator and background renderer.
/// Updated by the view whenever the caret moves, the selection changes, or the document
/// text changes (which also recomputes fenced-code-block ranges and table detection).
/// </summary>
public sealed class LivePreviewState
{
    private static readonly List<(int Start, int End)> Empty = new();

    public int CaretOffset { get; set; }
    public List<(int Start, int End)> Selections { get; set; } = Empty;
    public Dictionary<int, FenceInfo> Fences { get; set; } = new();

    /// <summary>HTML context of each line — the alignment of the block wrapping it and the inline tags
    /// still open when it starts (<see cref="MarkdownHtmlParser.ComputeLineStates"/>). Only lines that
    /// carry something are in the dictionary, so a note without HTML costs one empty lookup per line and
    /// nothing else.</summary>
    public Dictionary<int, HtmlLineState> HtmlLines { get; set; } = new();

    /// <summary>Term the editor's Buscar/Reemplazar panel is currently searching for — null/empty means
    /// no highlight. Set by the view whenever the panel is open and its search text/match-case changes, so
    /// <see cref="MarkdownBackgroundRenderer"/> can shade every match, not just the one the native text
    /// selection already marks as "current" (análisis §4.2, Fase 8).</summary>
    public string? SearchHighlightText { get; set; }
    public bool SearchHighlightMatchCase { get; set; }

    public FenceInfo GetFence(int lineNumber) =>
        Fences.TryGetValue(lineNumber, out var f) ? f : default;

    public HtmlLineState GetHtml(int lineNumber) =>
        HtmlLines.TryGetValue(lineNumber, out var state) ? state : HtmlLineState.Empty;

    public MdAlign GetAlignment(int lineNumber) => GetHtml(lineNumber).Align;

    /// <summary>Whether the document uses any HTML at all — lets the view skip the extra redraw that
    /// keeps centred/right-aligned lines correct across a resize when nothing is aligned.</summary>
    public bool HasAlignments => HtmlLines.Count > 0;

    /// <summary>Whether the caret sits inside [start,end] or any selection overlaps [start,end).</summary>
    public bool Intersects(int start, int end)
    {
        if (CaretOffset >= start && CaretOffset <= end) return true;
        foreach (var (s, e) in Selections)
        {
            if (s < end && e > start) return true;
        }
        return false;
    }

}
