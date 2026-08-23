using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Draft state backing the "Nuevo recordatorio" popup — mirrors
/// <see cref="KanbanCardEditorViewModel"/>'s calendar popover (same <see cref="KanbanCalendarDayViewModel"/>
/// day cells, reused as-is) but simplified for reminders: no priority/subtasks, and the time of day is
/// always present (defaults to 10:30) instead of Kanban's opt-in "agregar hora" toggle.</summary>
public sealed partial class ReminderEditorViewModel : ObservableObject
{
    public const int DescriptionMaxLength = 240;

    /// <summary>Curated set offered by the icon picker — every value confirmed to exist in
    /// Material.Icons.Avalonia 3.0.2.</summary>
    public static readonly MaterialIconKind[] IconChoices =
    [
        MaterialIconKind.BellRingOutline,
        MaterialIconKind.CalendarClockOutline,
        MaterialIconKind.FlagOutline,
        MaterialIconKind.EmailOutline,
        MaterialIconKind.CheckCircleOutline,
        MaterialIconKind.StarCircleOutline,
        MaterialIconKind.GiftOutline,
        MaterialIconKind.BriefcaseOutline
    ];

    public ObservableCollection<KanbanCalendarDayViewModel> CalendarDays { get; } = [];

    public ObservableCollection<ReminderIconOptionViewModel> IconOptions { get; } = [];

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private DateTime? _notifyDate = DateTime.Today;

    [ObservableProperty]
    private decimal _notifyHour = 10;

    [ObservableProperty]
    private decimal _notifyMinute = 30;

    [ObservableProperty]
    private MaterialIconKind _selectedIcon = MaterialIconKind.BellRingOutline;

    /// <summary>Recurrencia local (Fase 10, docs/analisis-proyecto/roadmap.md) — mismo enum y mismo patrón de
    /// selector segmentado que <see cref="PolarTaskEditorViewModel.SelectRecurrence"/>, para que un usuario
    /// sin cuenta Polar vinculada pueda crear recordatorios repetitivos sin recrearlos manualmente.</summary>
    [ObservableProperty]
    private PolarRecurrence _recurrence = PolarRecurrence.None;

    [ObservableProperty]
    private bool _isCalendarOpen;

    [ObservableProperty]
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public string DescriptionCounterLabel => $"{Description.Length}/{DescriptionMaxLength}";
    public bool CanSave => !string.IsNullOrWhiteSpace(Title) && NotifyDate is not null;

    // Was a hardcoded "dd/MM/yyyy" pattern — always European day/month order regardless of the active
    // culture, which reads backwards for en-US users (who expect M/d/yyyy). "d" is .NET's short-date
    // *pattern*, resolved against CultureInfo.CurrentCulture at call time — same fix already applied to
    // CalendarQuickCreateViewModel.DateDisplayLabel (análisis §4.4, Fase 8).
    public string NotifyDateDisplayLabel => NotifyDate is { } date
        ? $"{date.ToString("d", CultureInfo.CurrentCulture)} {NotifyHour:00}:{NotifyMinute:00}"
        : LocalizationService.Instance["Reminders_Editor_SelectDatePlaceholder"];

    /// <summary>Was <c>SpanishMonths[CalendarMonth.Month - 1]</c> — a hardcoded Spanish month-name array,
    /// one of the 6 duplicated copies documented in docs/agent-docs/internacionalizacion-i18n.md §1.7. Now
    /// derived from <see cref="CultureInfo"/> instead, composed through the same "{0} {1}" template key
    /// <see cref="CalendarQuickCreateViewModel.CalendarMonthLabel"/> already uses.</summary>
    public string CalendarMonthLabel => string.Format(
        CultureInfo.CurrentCulture,
        LocalizationService.Instance["Calendar_Header_MonthYear"],
        CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(CalendarMonth.Month).ToUpper(CultureInfo.CurrentCulture),
        CalendarMonth.Year);

    public ReminderEditorViewModel()
    {
        RebuildCalendarDays();
        foreach (var icon in IconChoices)
            IconOptions.Add(new ReminderIconOptionViewModel(icon, icon == SelectedIcon));
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(DescriptionCounterLabel));

    partial void OnNotifyDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(NotifyDateDisplayLabel));
        RebuildCalendarDays();
    }

    partial void OnNotifyHourChanged(decimal value) => OnPropertyChanged(nameof(NotifyDateDisplayLabel));

    partial void OnNotifyMinuteChanged(decimal value) => OnPropertyChanged(nameof(NotifyDateDisplayLabel));

    partial void OnCalendarMonthChanged(DateTime value)
    {
        OnPropertyChanged(nameof(CalendarMonthLabel));
        RebuildCalendarDays();
    }

    [RelayCommand]
    private void SelectIcon(MaterialIconKind icon)
    {
        SelectedIcon = icon;
        foreach (var option in IconOptions) option.IsSelected = option.Icon == icon;
    }

    [RelayCommand]
    private void SelectRecurrence(string recurrenceName)
    {
        if (Enum.TryParse<PolarRecurrence>(recurrenceName, out var recurrence)) Recurrence = recurrence;
    }

    [RelayCommand]
    private void ToggleCalendar()
    {
        if (!IsCalendarOpen)
        {
            var anchor = NotifyDate ?? DateTime.Today;
            CalendarMonth = new DateTime(anchor.Year, anchor.Month, 1);
            RebuildCalendarDays();
        }

        IsCalendarOpen = !IsCalendarOpen;
    }

    [RelayCommand]
    private void PreviousMonth() => CalendarMonth = CalendarMonth.AddMonths(-1);

    [RelayCommand]
    private void NextMonth() => CalendarMonth = CalendarMonth.AddMonths(1);

    [RelayCommand]
    private void SelectCalendarDay(KanbanCalendarDayViewModel day)
    {
        NotifyDate = day.Date;
        IsCalendarOpen = false;
    }

    [RelayCommand]
    private void SetNotifyDateToday()
    {
        CalendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        NotifyDate = DateTime.Today;
    }

    private void RebuildCalendarDays()
    {
        var firstOfMonth = new DateTime(CalendarMonth.Year, CalendarMonth.Month, 1);
        // Spanish week starts on Monday: shift DayOfWeek (Sunday=0..Saturday=6) so Monday=0.
        var offset = ((int)firstOfMonth.DayOfWeek + 6) % 7;
        var gridStart = firstOfMonth.AddDays(-offset);
        var today = DateTime.Today;

        CalendarDays.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            CalendarDays.Add(new KanbanCalendarDayViewModel(
                date,
                isCurrentMonth: date.Month == firstOfMonth.Month,
                isToday: date == today,
                isSelected: NotifyDate is { } notify && notify.Date == date));
        }
    }

    public DateTime BuildNotifyAtUtc()
    {
        var date = NotifyDate!.Value.Date.Add(new TimeSpan((int)NotifyHour, (int)NotifyMinute, 0));
        return DateTime.SpecifyKind(date, DateTimeKind.Local).ToUniversalTime();
    }
}
