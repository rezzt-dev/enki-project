using System;
using System.Collections.Generic;
using System.Linq;

namespace enki.ViewModels;

/// <summary>Assigns each <see cref="CalendarTimedItemViewModel"/> in a Semana/Día hour-grid column to one
/// of N side-by-side sub-columns, so items whose time ranges overlap sit next to each other instead of
/// drawing completely on top of one another (análisis §4.4, Fase 8 — no había ningún manejo de colisión
/// visual). Same overall shape as the classic "meeting rooms" scheduling algorithm: sort by start time,
/// merge transitively-overlapping items into clusters (a gap with no active item ends a cluster), then
/// greedily pack each cluster's items into the fewest sub-columns such that no two items sharing a
/// sub-column overlap in time. Doesn't attempt Google Calendar's further "widen an item that has no sibling
/// in the columns to its right" optimization — deliberately, to keep this a low-effort fix: every item
/// still gets a strictly non-overlapping slot, just not always the widest slot it could visually afford.</summary>
public static class CalendarTimedItemLayout
{
    public static void Apply(IReadOnlyList<CalendarTimedItemViewModel> items)
    {
        if (items.Count == 0) return;

        var ordered = items
            .Select(item => (Item: item, Start: item.TopOffset, End: item.TopOffset + item.Height))
            .OrderBy(x => x.Start)
            .ThenByDescending(x => x.End)
            .ToList();

        var cluster = new List<(CalendarTimedItemViewModel Item, double Start, double End)> { ordered[0] };
        var clusterEnd = ordered[0].End;

        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            if (current.Start >= clusterEnd)
            {
                AssignColumns(cluster);
                cluster = [];
                clusterEnd = current.End;
            }
            else
            {
                clusterEnd = Math.Max(clusterEnd, current.End);
            }
            cluster.Add(current);
        }
        AssignColumns(cluster);
    }

    private static void AssignColumns(List<(CalendarTimedItemViewModel Item, double Start, double End)> cluster)
    {
        // columnEnds[c] = end offset of the last item so far placed in sub-column c.
        var columnEnds = new List<double>();
        var placements = new List<(CalendarTimedItemViewModel Item, int Column)>(cluster.Count);

        foreach (var (item, start, end) in cluster)
        {
            var column = columnEnds.FindIndex(columnEndOffset => columnEndOffset <= start);
            if (column == -1)
            {
                column = columnEnds.Count;
                columnEnds.Add(end);
            }
            else
            {
                columnEnds[column] = end;
            }
            placements.Add((item, column));
        }

        var columnCount = columnEnds.Count;
        foreach (var (item, column) in placements)
            item.SetHorizontalLayout(column, columnCount);
    }
}
