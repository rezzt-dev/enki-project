using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>One day-header group of the Agenda view (docs/calendar-feature/04-vistas-y-navegacion.md §6).
/// <see cref="Build"/> is a pure static grouping function — no service dependency, so it's testable by
/// constructing <see cref="CalendarItemViewModel"/>s directly — and, per the same §6, only ever produces
/// groups for dates that actually have at least one item: days without any never show an empty group.</summary>
public sealed class CalendarAgendaGroupViewModel
{
    public DateTime Date { get; }
    public string HeaderLabel { get; }
    public IReadOnlyList<CalendarItemViewModel> Items { get; }

    private CalendarAgendaGroupViewModel(DateTime date, IReadOnlyList<CalendarItemViewModel> items)
    {
        Date = date;
        Items = items;

        // Was a literal Spanish concatenation ("LUNES 17 DE AGOSTO", hardcoding " DE " between the day
        // number and the month name) over two 6-idiom-duplicated arrays — see docs/agent-docs/
        // internacionalizacion-i18n.md §1.7. Now sourced from CultureInfo (so a 7th language needs zero
        // code changes here) and composed through a resource-key template so word order can vary per
        // language instead of always being "day DE month" (§1.3).
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
        var weekday = dtf.GetDayName(date.DayOfWeek).ToUpper(CultureInfo.CurrentCulture);
        var month = dtf.GetMonthName(date.Month).ToUpper(CultureInfo.CurrentCulture);
        HeaderLabel = string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Calendar_Header_FullDate"], weekday, date.Day, month);
    }

    /// <summary>Groups by local calendar date, ordered chronologically; within each day, all-day items
    /// come first and then by time — same criterion <see cref="Services.CalendarAggregationService"/>
    /// already applies globally, re-applied per-group here so this method's output is correct regardless
    /// of whether the caller's input list happens to be pre-sorted.</summary>
    public static IReadOnlyList<CalendarAgendaGroupViewModel> Build(IEnumerable<CalendarItemViewModel> items)
    {
        return items
            .GroupBy(i => i.MomentLocal.Date)
            .OrderBy(g => g.Key)
            .Select(g => new CalendarAgendaGroupViewModel(
                g.Key,
                g.OrderBy(i => i.IsAllDay ? 0 : 1).ThenBy(i => i.MomentLocal).ToList()))
            .ToList();
    }
}
