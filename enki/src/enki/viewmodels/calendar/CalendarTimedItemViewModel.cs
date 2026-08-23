using System;

namespace enki.ViewModels;

/// <summary>Positions a timed <see cref="CalendarItemViewModel"/> inside a Semana/Día hour grid
/// (docs/calendar-feature/04-vistas-y-navegacion.md §5) — pure pixel math, no service dependency, so it's
/// trivially unit-testable by constructing a <see cref="CalendarItemViewModel"/> directly. An item without
/// a real <see cref="CalendarItemViewModel.DurationMinutes"/> still gets the old fixed minimum visual
/// height (a drawing convention only, never written back); a Kanban card that does carry one gets its real
/// height instead, floored at <see cref="MinimumRealDurationFloorMinutes"/> so a very short franja horaria
/// (e.g. 5 minutos) stays tall enough to read its title.</summary>
public sealed class CalendarTimedItemViewModel
{
    /// <summary>Floor applied to a *real* persisted duration so the block stays tall enough to fit the
    /// timed chip's title row plus the separate start/end time labels pinned to its top and bottom edges
    /// (docs/calendar-feature/10-diseno-visual-y-componentes.md §4) — a shorter real duration is still
    /// shown accurately via those two labels even when the block itself can't shrink to match exactly.</summary>
    public const double MinimumRealDurationFloorMinutes = 30;

    public CalendarItemViewModel Item { get; }
    public double TopOffset { get; }
    public double Height { get; }

    /// <summary>Horizontal slot this item occupies, as a 0..1 fraction of the day column's width — set by
    /// <see cref="CalendarTimedItemLayout.Apply"/> once every item in the column is known (a single item
    /// can't compute this on its own; it needs to see its siblings' time ranges too). Defaults to a full-
    /// width, zero-offset slot so an item never rendered through the layout pass still draws sensibly.
    /// <see cref="enki.Controls.CalendarTimedItemsPanel"/> reads both to arrange each item's horizontal
    /// slice.</summary>
    public double LeftFraction { get; private set; }
    public double WidthFraction { get; private set; } = 1.0;

    /// <summary>The effective duration (in minutes) this block is drawn with — real duration when the
    /// item has one (floored), otherwise the shared no-duration convention. Exposed so
    /// CalendarView.axaml.cs's resize gesture has a starting value to adjust from without recomputing this
    /// floor/fallback logic itself.</summary>
    public int EffectiveDurationMinutes { get; }

    public CalendarTimedItemViewModel(CalendarItemViewModel item, double hourRowHeight, double minimumVisualHeightMinutes)
    {
        Item = item;
        var minutesFromMidnight = item.MomentLocal.TimeOfDay.TotalMinutes;
        TopOffset = minutesFromMidnight / 60.0 * hourRowHeight;

        var effectiveMinutes = item.DurationMinutes is { } real
            ? Math.Max(real, MinimumRealDurationFloorMinutes)
            : minimumVisualHeightMinutes;
        EffectiveDurationMinutes = (int)effectiveMinutes;
        Height = effectiveMinutes / 60.0 * hourRowHeight;
    }

    /// <summary>Called once per item by <see cref="CalendarTimedItemLayout.Apply"/>; not meant to be
    /// called from anywhere else.</summary>
    internal void SetHorizontalLayout(int columnIndex, int columnCount)
    {
        WidthFraction = 1.0 / columnCount;
        LeftFraction = columnIndex * WidthFraction;
    }
}
