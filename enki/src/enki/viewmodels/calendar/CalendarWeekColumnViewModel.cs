using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>One column of the Semana view, or the single column of the Día view (they share this type —
/// Día is simply a Semana with one <see cref="CalendarViewModel.WeekColumns"/> entry, see
/// docs/calendar-feature/04-vistas-y-navegacion.md §4). All-day items float in the fixed header strip
/// (§3); timed items get pixel-positioned in the hour grid via <see cref="CalendarTimedItemViewModel"/>.
/// <see cref="NowOffsetY"/> is the only mutable piece — <see cref="CalendarViewModel"/> refreshes it on a
/// timer without rebuilding the whole column, so the "now" line creeps forward live without re-flowing
/// the rest of the grid (docs/calendar-feature/10-diseno-visual-y-componentes.md §3).</summary>
public sealed partial class CalendarWeekColumnViewModel : ObservableObject
{
    /// <summary>Height in pixels of one hour row — shared by the view's hour labels and every
    /// <see cref="CalendarTimedItemViewModel"/>'s offset/height math so they can never drift apart.</summary>
    public const double HourRowHeight = 60;

    /// <summary>Fixed minimum visual duration for any timed item — docs/calendar-feature/
    /// 04-vistas-y-navegacion.md §5: none of the three source models has a real duration field.</summary>
    public const double MinimumVisualHeightMinutes = 40;

    public DateTime Date { get; }
    public string DayLabel { get; }
    public bool IsToday { get; }
    public IReadOnlyList<CalendarItemViewModel> AllDayItems { get; }
    public IReadOnlyList<CalendarTimedItemViewModel> TimedItems { get; }

    /// <summary>Pixel Y offset of the "now" line within this column's hour grid, or <c>null</c> when this
    /// column isn't today — only ever one column has a non-null value at a time.</summary>
    [ObservableProperty]
    private double? _nowOffsetY;

    public CalendarWeekColumnViewModel(DateTime date, IReadOnlyList<CalendarItemViewModel> dayItems, DateTime nowLocal)
    {
        Date = date.Date;
        IsToday = Date == nowLocal.Date;
        // Was a 4th, undocumented copy of the hardcoded-Spanish-weekday-array bug described in
        // docs/agent-docs/internacionalizacion-i18n.md §1.7 (that section only counts 6 across the rest
        // of the app) — same fix as the other three in this batch: CultureInfo instead of a fixed array,
        // composed through a resource-key template instead of a raw interpolation (§1.3).
        var abbreviatedDayName = CultureInfo.CurrentCulture.DateTimeFormat
            .GetAbbreviatedDayName(Date.DayOfWeek).ToUpper(CultureInfo.CurrentCulture);
        DayLabel = string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Calendar_Header_WeekdayDay"], abbreviatedDayName, Date.Day);

        AllDayItems = dayItems.Where(i => i.IsAllDay).ToList();
        TimedItems = dayItems
            .Where(i => !i.IsAllDay)
            .Select(i => new CalendarTimedItemViewModel(i, HourRowHeight, MinimumVisualHeightMinutes))
            .ToList();

        RefreshNow(nowLocal);
    }

    /// <summary>Recomputes <see cref="NowOffsetY"/> for a fresh "now" without touching <see cref="AllDayItems"/>/
    /// <see cref="TimedItems"/> — called both from the constructor and from <see cref="CalendarViewModel"/>'s
    /// periodic timer.</summary>
    public void RefreshNow(DateTime nowLocal)
    {
        NowOffsetY = IsToday ? nowLocal.TimeOfDay.TotalMinutes / 60.0 * HourRowHeight : null;
    }
}
