using System;
using System.Globalization;
using System.Linq;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Shared localized label for a <see cref="PolarRecurrence"/> value — extracted from
/// <c>PolarTaskRowViewModel</c> (Fase 10, docs/analisis-proyecto/roadmap.md) so the same "cada día"/"cada
/// semana"/"lun y mié"/"día 1 de cada mes" wording is built once and reused by every screen that shows a
/// recurrence (Kanban's task detail/editor and now the Recordatorios editor/list, which gained its own
/// local-only recurrence — see <see cref="Reminder.Recurrence"/>) instead of duplicating the switch.</summary>
public static class PolarRecurrenceFormatter
{
    public static string Format(PolarRecurrence recurrence) => recurrence switch
    {
        PolarRecurrence.Daily => LocalizationService.Instance["Polar_Recurrence_Daily"],
        PolarRecurrence.Weekly => LocalizationService.Instance["Polar_Recurrence_Weekly"],
        PolarRecurrence.Monthly => LocalizationService.Instance["Polar_Recurrence_Monthly"],
        PolarRecurrence.MonWed => BuildDaysOfWeekLabel(DayOfWeek.Monday, DayOfWeek.Wednesday),
        PolarRecurrence.FirstDayMonth => string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Polar_Recurrence_DayOfMonth"], 1),
        _ => string.Empty
    };

    /// <summary>Builds a day-of-week combination label (e.g. "MON AND WED") from the active
    /// <see cref="CultureInfo"/> instead of a hardcoded Spanish lookup table — see
    /// docs/agent-docs/internacionalizacion-i18n.md §1.7. Only ever called with exactly 2 days today
    /// (<see cref="PolarRecurrence.MonWed"/> is the only combination the wire contract defines), joined with
    /// <c>Polar_Recurrence_DaysJoiner</c>; the whole result is uppercased to match the all-caps chip style
    /// every caller of this formatter uses regardless of what casing <see cref="CultureInfo.DateTimeFormat"/>
    /// returns.</summary>
    private static string BuildDaysOfWeekLabel(params DayOfWeek[] days)
    {
        var culture = CultureInfo.CurrentCulture;
        var joiner = LocalizationService.Instance["Polar_Recurrence_DaysJoiner"];
        var label = string.Join(joiner, days.Select(d => culture.DateTimeFormat.GetAbbreviatedDayName(d)));
        return culture.TextInfo.ToUpper(label);
    }
}
