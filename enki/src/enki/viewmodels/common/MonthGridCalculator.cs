using System;

namespace enki.ViewModels;

/// <summary>Shared "6 weeks × 7 days = 42 fixed cells" grid math (docs/calendar-feature/
/// 11-arquitectura-tecnica.md §4, opción b elegida: extraer solo el cálculo puro a un método estático
/// compartido en vez de mover <see cref="KanbanCalendarDayViewModel"/> entero). Used by both the Kanban
/// due-date picker (<see cref="KanbanCardEditorViewModel"/>) and the calendar's month view
/// (<see cref="CalendarViewModel"/>) so the two month grids can never disagree on which 42 dates a given
/// month renders.</summary>
public static class MonthGridCalculator
{
    /// <summary>Always 42 consecutive dates covering <paramref name="month"/> plus enough padding from
    /// the surrounding months to fill 6 full weeks. Defaults to the Spanish convention (week starts on
    /// Monday: shifts <see cref="DayOfWeek"/> (Sunday=0..Saturday=6) so Monday=0) — the Kanban due-date
    /// picker and the calendar's quick-create popover both call this with the default, so only the
    /// calendar page itself (<c>AppSettings.CalendarWeekStartsOnMonday</c>, docs/calendar-feature/
    /// 09-configuracion-y-ajustes.md §1) ever passes <paramref name="startOnMonday"/> as <c>false</c>.</summary>
    public static DateTime[] BuildDates(DateTime month, bool startOnMonday = true)
    {
        var firstOfMonth = new DateTime(month.Year, month.Month, 1);
        var offset = startOnMonday ? ((int)firstOfMonth.DayOfWeek + 6) % 7 : (int)firstOfMonth.DayOfWeek;
        var gridStart = firstOfMonth.AddDays(-offset);

        var dates = new DateTime[42];
        for (var i = 0; i < 42; i++) dates[i] = gridStart.AddDays(i);
        return dates;
    }
}
