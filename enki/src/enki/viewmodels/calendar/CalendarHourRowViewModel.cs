namespace enki.ViewModels;

/// <summary>One fixed row of the Semana/Día hour grid (00:00–23:00) — rebuilt whenever
/// <c>AppSettings.CalendarWorkingHoursStart/End</c>/<c>CalendarTimeFormat24h</c> changes
/// (docs/calendar-feature/09-configuracion-y-ajustes.md §1, see <see cref="CalendarViewModel.RebuildHourRows"/>),
/// never on a plain data refresh.</summary>
public sealed class CalendarHourRowViewModel(int hour, bool isWorkingHour, bool use24h = true)
{
    /// <summary>Used by the calendar view's code-behind (Fase 3, docs/calendar-feature/
    /// 05-interacciones-y-microinteracciones.md §2/§3) to resolve which hour a click or drop on this row
    /// targets, combined with the owning column's date.</summary>
    public int Hour { get; } = hour;

    public string Label => use24h ? $"{Hour:00}:00" : FormatAmPm(Hour);
    public bool IsWorkingHour { get; } = isWorkingHour;

    private static string FormatAmPm(int hour24)
    {
        var period = hour24 < 12 ? "AM" : "PM";
        var hour12 = hour24 % 12 == 0 ? 12 : hour24 % 12;
        return $"{hour12} {period}";
    }
}
