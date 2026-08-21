using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>One of the 42 fixed cells in the calendar's month view (docs/calendar-feature/
/// 04-vistas-y-navegacion.md §2) — same shape as <see cref="KanbanCalendarDayViewModel"/> plus the
/// <see cref="CalendarItemViewModel"/>s that fall on this day. Rebuilt from scratch alongside the rest of
/// <see cref="CalendarViewModel.Days"/> on every refresh, so this holds a plain list rather than an
/// observable one.</summary>
public sealed partial class CalendarMonthDayViewModel : ObservableObject
{
    /// <summary>3–4 chips fit comfortably before a cell needs to grow — docs/calendar-feature/
    /// 04-vistas-y-navegacion.md §2 recommends that range; the rest collapse behind "+N más".</summary>
    private const int MaxVisibleItems = 3;

    public DateTime Date { get; }
    public string DayLabel => Date.Day.ToString(CultureInfo.CurrentCulture);
    public bool IsCurrentMonth { get; }
    public bool IsToday { get; }

    /// <summary>True when this cell is <see cref="CalendarViewModel.ReferenceDate"/> — driven by clicking
    /// a day in the sidebar mini-calendar (docs/calendar-feature/04-vistas-y-navegacion.md §7: "clic en un
    /// día... simplemente resalta el día si está en Mes"). Purely a highlight, not a click target of its
    /// own in this phase — see Fase 3 for cell-level interactions.</summary>
    public bool IsSelected { get; }

    /// <summary>Rightmost of the grid's columns (7, or 5 when <c>AppSettings.CalendarShowWeekends</c> is
    /// off — docs/calendar-feature/09-configuracion-y-ajustes.md §1) — its cell skips the right-edge seam
    /// margin so it doesn't double up with Border.calendar-month-grid's own border, see Styles.axaml.</summary>
    public bool IsLastColumn { get; }
    /// <summary>Bottom of the fixed 6 rows (always 6 regardless of column count — see
    /// <see cref="CalendarViewModel.RebuildMonth"/>) — same seam-doubling fix as <see cref="IsLastColumn"/>,
    /// but for the bottom edge.</summary>
    public bool IsLastRow { get; }

    public IReadOnlyList<CalendarItemViewModel> Items { get; }
    public IReadOnlyList<CalendarItemViewModel> VisibleItems { get; }
    public int OverflowCount { get; }
    public bool HasOverflow => OverflowCount > 0;
    public string OverflowLabel => string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Calendar_Month_OverflowMore"], OverflowCount);

    /// <summary>Header of the "+N más" overflow popover (CalendarView.axaml) — was a fixed-word-order
    /// <c>StringFormat='DÍA {0}'</c> binding directly on <see cref="DayLabel"/>; moved to a computed
    /// property per docs/agent-docs/internacionalizacion-i18n.md §1.3 so the resource key can reorder
    /// freely instead of the XAML always prefixing "DAY".</summary>
    public string OverflowPopoverHeaderLabel => string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Calendar_Month_DayPopoverHeader"], DayLabel);

    public CalendarMonthDayViewModel(DateTime date, bool isCurrentMonth, bool isToday, bool isSelected, IReadOnlyList<CalendarItemViewModel> items, int index, int columns = 7)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        IsSelected = isSelected;
        IsLastColumn = index % columns == columns - 1;
        IsLastRow = index >= 5 * columns;
        Items = items;
        VisibleItems = items.Take(MaxVisibleItems).ToList();
        OverflowCount = Math.Max(0, items.Count - MaxVisibleItems);
    }
}
