using System;
using System.Globalization;

namespace enki.ViewModels;

/// <summary>One cell of the sidebar mini-calendar (docs/calendar-feature/04-vistas-y-navegacion.md §7) —
/// same 42-cell month grid as <see cref="CalendarMonthDayViewModel"/> but without chips: just a dot
/// indicator (<see cref="HasItems"/>) and, unlike the main grid, a notion of "selected" tied to
/// <see cref="CalendarViewModel.ReferenceDate"/> since this is the one surface that actually drives that
/// date via a click.</summary>
public sealed class CalendarMiniDayViewModel(DateTime date, bool isCurrentMonth, bool isToday, bool isSelected, bool hasItems)
{
    public DateTime Date { get; } = date;
    public string DayLabel => Date.Day.ToString(CultureInfo.CurrentCulture);
    public bool IsCurrentMonth { get; } = isCurrentMonth;
    public bool IsToday { get; } = isToday;
    public bool IsSelected { get; } = isSelected;
    public bool HasItems { get; } = hasItems;
}
