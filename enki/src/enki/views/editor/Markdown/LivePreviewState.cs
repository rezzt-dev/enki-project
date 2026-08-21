using System.Collections.Generic;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Shared state consumed by the colorizer, element generator and background renderer.
/// Updated by the view whenever the caret moves, the selection changes, or the document
/// text changes (which also recomputes fenced-code-block ranges).
/// </summary>
public sealed class LivePreviewState
{
    private static readonly List<(int Start, int End)> Empty = new();

    public int CaretOffset { get; set; }
    public List<(int Start, int End)> Selections { get; set; } = Empty;
    public Dictionary<int, FenceInfo> Fences { get; set; } = new();

    public FenceInfo GetFence(int lineNumber) =>
        Fences.TryGetValue(lineNumber, out var f) ? f : default;

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
