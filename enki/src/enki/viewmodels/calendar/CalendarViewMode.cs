namespace enki.ViewModels;

/// <summary>The four calendar surfaces of docs/calendar-feature/04-vistas-y-navegacion.md — switching
/// between them keeps <see cref="CalendarViewModel.ReferenceDate"/> unchanged (§9).</summary>
public enum CalendarViewMode
{
    Month,
    Week,
    Day,
    Agenda
}
