using System;

namespace enki.ViewModels;

/// <summary>Shared "Monday-start week" math (docs/calendar-feature/04-vistas-y-navegacion.md §3/§4), same
/// spirit as <see cref="MonthGridCalculator"/>: a pure, stateless calculation extracted so the calendar's
/// Semana/Día views and any future consumer can never disagree on which day starts a given week.</summary>
public static class WeekGridCalculator
{
    /// <summary>Start of the week containing <paramref name="date"/> — Monday by default (same
    /// Sunday=0..Saturday=6 shift used by <see cref="MonthGridCalculator.BuildDates"/> so both grids agree
    /// on where a week starts), or Sunday when <see cref="Models.AppSettings.CalendarWeekStartsOnMonday"/>
    /// is off (docs/calendar-feature/09-configuracion-y-ajustes.md §1).</summary>
    public static DateTime StartOfWeek(DateTime date, bool startOnMonday = true)
    {
        var offset = startOnMonday ? ((int)date.Date.DayOfWeek + 6) % 7 : (int)date.Date.DayOfWeek;
        return date.Date.AddDays(-offset);
    }
}
