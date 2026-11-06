using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>One cell in the due-date calendar grid (see KanbanCardEditorViewModel.CalendarDays) —
/// always 42 of these per month view (6 fixed rows) so paging months never resizes the popover.</summary>
public sealed partial class KanbanCalendarDayViewModel : ObservableObject
{
    public DateTime Date { get; }
    public string DayLabel => Date.Day.ToString(CultureInfo.CurrentCulture);
    public bool IsCurrentMonth { get; }
    public bool IsToday { get; }

    [ObservableProperty]
    private bool _isSelected;

    public KanbanCalendarDayViewModel(DateTime date, bool isCurrentMonth, bool isToday, bool isSelected)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        _isSelected = isSelected;
    }
}
