using System;

namespace enki.ViewModels;

/// <summary>Shared title-matching tiers used by both the command palette
/// (<see cref="CommandPaletteViewModel"/>) and the quick switcher (<see cref="QuickSwitcherViewModel"/>) —
/// extracted so the two search boxes can never silently drift apart on what counts as an exact/prefix/
/// substring match (análisis alfa-3 §4.5).</summary>
public static class TitleFuzzyScorer
{
    /// <summary>0 (no match) up to 1000 (exact match) — an exact title match always outranks a prefix
    /// match, which always outranks a plain substring match.</summary>
    public static int ScoreTitle(string title, string query)
    {
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 500;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 250;
        return 0;
    }
}
