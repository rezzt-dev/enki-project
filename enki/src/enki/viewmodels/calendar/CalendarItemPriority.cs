namespace enki.ViewModels;

/// <summary>Shared priority scale a <see cref="CalendarItemViewModel"/> maps both <c>KanbanCard.Priority</c>
/// and <c>PolarTask.Priority</c> onto (docs/calendar-feature/07-filtros-busqueda-y-capas.md §3: "Alta/Media/
/// Baja/Ninguna"), so the priority filter can compare Kanban and Polar items with a single enum instead of
/// juggling a <c>KanbanPriority</c> and a raw <c>int</c> side by side. <see cref="Reminder"/> has no
/// priority of its own and always maps to <see cref="None"/> — the filter explicitly exempts Reminder items
/// from priority filtering rather than treating that as "matches Ninguna" (see
/// <see cref="CalendarItemFilter"/>).</summary>
public enum CalendarItemPriority
{
    None,
    Low,
    Medium,
    High
}
