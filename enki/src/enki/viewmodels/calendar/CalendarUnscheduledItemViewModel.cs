using System.Collections.Generic;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Row in the "sin programadas" sidebar panel (docs/calendar-feature/04-vistas-y-navegacion.md
/// §8) — a <c>KanbanCard</c>/<c>PolarTask</c>/<c>PolarSubtask</c> with <c>DueDateUtc == null</c>. Deliberately
/// a separate shape from <see cref="CalendarItemViewModel"/> rather than that record with a nullable
/// <c>MomentUtc</c>: every positioned-item computation on <see cref="CalendarItemViewModel"/> (<c>MomentLocal</c>,
/// <c>IsPastDue</c>, <c>TimeLabel</c>...) assumes a real moment, and an unscheduled item has none of that —
/// it only has enough to render a draggable row and carry a source reference for
/// <see cref="CalendarViewModel.MoveItem"/> once dropped onto the grid. <c>Reminder</c> never appears here
/// (docs/calendar-feature/03-glosario-y-modelo-de-datos.md §6: <c>NotifyAtUtc</c> is never null).</summary>
public sealed record CalendarUnscheduledItemViewModel
{
    public required string SourceId { get; init; }
    public required CalendarItemSourceType SourceType { get; init; }
    public required string Title { get; init; }
    public required string LayerColorHex { get; init; }
    public required MaterialIconKind IconKind { get; init; }

    /// <summary>Same layer key as <see cref="CalendarItemViewModel.LayerKey"/> — the panel respects the
    /// same active capas as the grid (docs/calendar-feature/07-filtros-busqueda-y-capas.md §4).</summary>
    public required string LayerKey { get; init; }

    /// <summary>Parent Kanban column / Polar list title, shown as secondary context under the title —
    /// same role as <see cref="CalendarItemViewModel.ParentContextLabel"/>.</summary>
    public string ParentContextLabel { get; init; } = string.Empty;

    public bool HasParentContext => !string.IsNullOrWhiteSpace(ParentContextLabel);

    // The three fields below exist only so the panel can go through the same
    // CalendarItemFilter.MatchesSecondaryFilters rule as the grid (docs/calendar-feature/
    // 07-filtros-busqueda-y-capas.md §4: "los filtros secundarios también aplican a ese panel") — an
    // unscheduled item doesn't render a priority/tag/completed indicator of its own in this phase.
    public CalendarItemPriority Priority { get; init; } = CalendarItemPriority.None;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool IsCompleted { get; init; }
}
