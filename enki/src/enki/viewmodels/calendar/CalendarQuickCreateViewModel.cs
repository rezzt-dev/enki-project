using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Which of the three source types the quick-create popover is currently building — Polar tasks
/// are excluded when <see cref="AppSettings.PolarLinked"/> is off (docs/calendar-feature/
/// 06-creacion-y-edicion-de-items.md §1: "ese botón directamente no se renderiza").</summary>
public enum CalendarQuickCreateType
{
    Kanban,
    PolarTask,
    Reminder
}

/// <summary>Draft state backing the calendar's quick-create popover (docs/calendar-feature/
/// 06-creacion-y-edicion-de-items.md §1) — deliberately the smallest possible form (title + date/time +
/// destination column/list), same "capture rápido, refina después" principle as TickTick/Fantastical's
/// quick-add. Mirrors the date-picker shape of <see cref="KanbanCardEditorViewModel"/>/
/// <see cref="PolarTaskEditorViewModel"/>/<see cref="ReminderEditorViewModel"/> so the popover feels like
/// the same control everywhere in the app, but never reimplements their full field set — "más opciones"
/// hands off to whichever of those three owns the rest (see <see cref="CalendarViewModel.SaveQuickCreateAndEditMoreCommand"/>).</summary>
public sealed partial class CalendarQuickCreateViewModel : ObservableObject
{
    /// <summary>Monday-first, matching <see cref="MonthGridCalculator.BuildDates(DateTime,bool)"/>'s
    /// default (this popover's date-picker grid always calls it with the default) — backs
    /// <see cref="WeekdayHeaders"/> below.</summary>
    private static readonly DayOfWeek[] WeekOrderMondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public bool IsPolarAvailable { get; }

    public ObservableCollection<KanbanBoard> Boards { get; } = [];
    [ObservableProperty] private KanbanBoard? _selectedBoard;
    public bool HasMultipleBoards => Boards.Count > 1;
    public void SetBoards(IEnumerable<KanbanBoard> boards, KanbanBoard defaultBoard)
    {
        Boards.Clear();
        foreach (var board in boards) Boards.Add(board);
        SelectedBoard = defaultBoard;
        OnPropertyChanged(nameof(HasMultipleBoards));
    }
    partial void OnSelectedBoardChanged(KanbanBoard? value)
    {
        Columns.Clear();
        if (value is not null) foreach (var column in value.Columns) Columns.Add(column);
        SelectedColumn = Columns.FirstOrDefault(c => c.Role == CoreColumnRole.Backlog) ?? Columns.FirstOrDefault();
    }

    public ObservableCollection<KanbanColumn> Columns { get; } = [];
    public ObservableCollection<PolarTaskList> PolarLists { get; } = [];
    public ObservableCollection<KanbanCalendarDayViewModel> CalendarDays { get; } = [];

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private CalendarQuickCreateType _selectedType = CalendarQuickCreateType.Kanban;

    [ObservableProperty]
    private DateTime _dueDate;

    /// <summary>Pre-checked when the popover was opened from a Semana/Día hour slot (a concrete time was
    /// clicked) — off when opened from a Mes day cell or the "+" toolbar button, leaving the item all-day
    /// same as the equivalent Kanban/Polar/Reminder create flows default to.</summary>
    [ObservableProperty]
    private bool _hasTime;

    [ObservableProperty]
    private decimal _hour = 9;

    [ObservableProperty]
    private decimal _minute;

    /// <summary>Opt-in franja horaria (docs/agent-docs/calendar-feature/04-vistas-y-navegacion.md §5) —
    /// mirrors <c>KanbanCardEditorViewModel.HasDuration</c>, pero aquí vale para los tres tipos: los tres
    /// servicios de destino aceptan un <c>DurationMinutes</c> local (ver
    /// <c>CalendarViewModel.CreateFromQuickCreate</c>), así que lo único que la condiciona es
    /// <see cref="HasTime"/> — una franja sin punto de partida no significa nada, y por eso
    /// <see cref="OnHasTimeChanged"/> la fuerza a apagarse cuando se quita la hora.</summary>
    [ObservableProperty]
    private bool _hasDuration;

    [ObservableProperty]
    private int _durationMinutes = 30;

    [ObservableProperty]
    private bool _isCalendarOpen;

    [ObservableProperty]
    private DateTime _calendarMonth;

    [ObservableProperty]
    private KanbanColumn? _selectedColumn;

    [ObservableProperty]
    private PolarTaskList? _selectedPolarList;

    // Was a hardcoded "dd/MM/yyyy" pattern — always European day/month order regardless of the active
    // culture, which reads backwards for en-US users (who expect M/d/yyyy). "d" is .NET's short-date
    // *pattern*, resolved against CultureInfo.CurrentCulture at call time, so it already picks up
    // whatever LocalizationService.SetLanguage set as the ambient culture — see docs/agent-docs/
    // internacionalizacion-i18n.md §4.6.
    public string DateDisplayLabel => HasTime
        ? $"{DueDate.ToString("d", CultureInfo.CurrentCulture)} {Hour:00}:{Minute:00}"
        : DueDate.ToString("d", CultureInfo.CurrentCulture);

    public string CalendarMonthLabel => string.Format(
        CultureInfo.CurrentCulture,
        LocalizationService.Instance["Calendar_Header_MonthYear"],
        CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(CalendarMonth.Month).ToUpper(CultureInfo.CurrentCulture),
        CalendarMonth.Year);

    /// <summary>3-letter (or culture-equivalent) weekday header row for the popover's own inline
    /// date-picker grid — was 7 static <c>TextBlock</c>s in CalendarView.axaml hardcoding "LU"/"MA"/"MI"...
    /// (another instance of the array-duplication bug documented in docs/agent-docs/
    /// internacionalizacion-i18n.md §1.7); now built once from <see cref="CultureInfo"/> instead.</summary>
    public IReadOnlyList<string> WeekdayHeaders { get; private set; } = BuildWeekdayHeaders();

    private static List<string> BuildWeekdayHeaders()
    {
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
        return WeekOrderMondayFirst.Select(d => dtf.GetShortestDayName(d).ToUpper(CultureInfo.CurrentCulture)).ToList();
    }

    /// <summary>Duration only makes sense when the item has a real start time. Kanban cards, Polar tasks
    /// and reminders can all carry a local <c>DurationMinutes</c> for the calendar grid.</summary>
    public bool CanShowDuration => HasTime;

    public string DurationRangeLabel => CanShowDuration && HasDuration
        ? $"{Hour:00}:{Minute:00} – {new DateTime(1, 1, 1, (int)Hour, (int)Minute, 0).AddMinutes(DurationMinutes):HH:mm}"
        : string.Empty;

    private int MaxDurationMinutes => Math.Max(5, 24 * 60 - ((int)Hour * 60 + (int)Minute));

    public bool IsKanbanType => SelectedType == CalendarQuickCreateType.Kanban;
    public bool IsPolarType => SelectedType == CalendarQuickCreateType.PolarTask;
    public bool IsReminderType => SelectedType == CalendarQuickCreateType.Reminder;

    public bool CanSave => !string.IsNullOrWhiteSpace(Title)
        && (!IsKanbanType || SelectedColumn is not null)
        && (!IsPolarType || SelectedPolarList is not null);

    public DateTime CombinedDueDateUtc
    {
        get
        {
            var local = HasTime ? DueDate.Date.Add(new TimeSpan((int)Hour, (int)Minute, 0)) : DueDate.Date;
            return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        }
    }

    /// <param name="initialLocalDate">The day clicked (Mes cell) or the day of the slot clicked
    /// (Semana/Día) — always a concrete date, never null: the "sin fecha" panel is Fase 4 scope
    /// (docs/calendar-feature/12-plan-de-implementacion-por-fases.md), so quick-create always lands
    /// somewhere on the grid for now.</param>
    /// <param name="initialLocalTime">Non-null only when opened from an hour slot in Semana/Día.</param>
    public CalendarQuickCreateViewModel(
        DateTime initialLocalDate, TimeSpan? initialLocalTime,
        System.Collections.Generic.IReadOnlyList<KanbanColumn> columns,
        System.Collections.Generic.IReadOnlyList<PolarTaskList> polarLists,
        bool isPolarAvailable)
    {
        IsPolarAvailable = isPolarAvailable;
        foreach (var column in columns) Columns.Add(column);
        foreach (var list in polarLists) PolarLists.Add(list);

        _selectedColumn = Columns.FirstOrDefault();
        _selectedPolarList = PolarLists.FirstOrDefault();

        _dueDate = initialLocalDate.Date;
        _calendarMonth = new DateTime(_dueDate.Year, _dueDate.Month, 1);
        if (initialLocalTime is { } time)
        {
            _hasTime = true;
            _hour = time.Hours;
            _minute = time.Minutes;
        }

        RebuildCalendarDays();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(DateDisplayLabel));
        OnPropertyChanged(nameof(CalendarMonthLabel));
        WeekdayHeaders = BuildWeekdayHeaders();
        OnPropertyChanged(nameof(WeekdayHeaders));
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnSelectedColumnChanged(KanbanColumn? value) => OnPropertyChanged(nameof(CanSave));

    partial void OnSelectedPolarListChanged(PolarTaskList? value) => OnPropertyChanged(nameof(CanSave));

    partial void OnSelectedTypeChanged(CalendarQuickCreateType value)
    {
        OnPropertyChanged(nameof(IsKanbanType));
        OnPropertyChanged(nameof(IsPolarType));
        OnPropertyChanged(nameof(IsReminderType));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanShowDuration));
        OnPropertyChanged(nameof(DurationRangeLabel));
    }

    partial void OnDueDateChanged(DateTime value)
    {
        OnPropertyChanged(nameof(DateDisplayLabel));
        RebuildCalendarDays();
    }

    partial void OnHasTimeChanged(bool value)
    {
        OnPropertyChanged(nameof(DateDisplayLabel));
        if (!value) HasDuration = false;
        OnPropertyChanged(nameof(CanShowDuration));
        OnPropertyChanged(nameof(DurationRangeLabel));
    }

    partial void OnHourChanged(decimal value)
    {
        OnPropertyChanged(nameof(DateDisplayLabel));
        DurationMinutes = Math.Min(DurationMinutes, MaxDurationMinutes);
        OnPropertyChanged(nameof(DurationRangeLabel));
    }

    partial void OnMinuteChanged(decimal value)
    {
        OnPropertyChanged(nameof(DateDisplayLabel));
        DurationMinutes = Math.Min(DurationMinutes, MaxDurationMinutes);
        OnPropertyChanged(nameof(DurationRangeLabel));
    }

    partial void OnHasDurationChanged(bool value) => OnPropertyChanged(nameof(DurationRangeLabel));

    partial void OnDurationMinutesChanged(int value) => OnPropertyChanged(nameof(DurationRangeLabel));

    [RelayCommand]
    private void IncrementDuration() => DurationMinutes = Math.Min(DurationMinutes + 5, MaxDurationMinutes);

    [RelayCommand]
    private void DecrementDuration() => DurationMinutes = Math.Max(DurationMinutes - 5, 5);

    partial void OnCalendarMonthChanged(DateTime value)
    {
        OnPropertyChanged(nameof(CalendarMonthLabel));
        RebuildCalendarDays();
    }

    [RelayCommand]
    private void SelectType(string typeName)
    {
        if (Enum.TryParse<CalendarQuickCreateType>(typeName, out var type)) SelectedType = type;
    }

    [RelayCommand]
    private void ToggleCalendar() => IsCalendarOpen = !IsCalendarOpen;

    [RelayCommand]
    private void PreviousMonth() => CalendarMonth = CalendarMonth.AddMonths(-1);

    [RelayCommand]
    private void NextMonth() => CalendarMonth = CalendarMonth.AddMonths(1);

    [RelayCommand]
    private void SelectCalendarDay(KanbanCalendarDayViewModel day)
    {
        DueDate = day.Date;
        IsCalendarOpen = false;
    }

    [RelayCommand]
    private void SetToday()
    {
        CalendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        DueDate = DateTime.Today;
    }

    [RelayCommand]
    private void SelectColumn(KanbanColumn column) => SelectedColumn = column;

    [RelayCommand]
    private void SelectPolarList(PolarTaskList list) => SelectedPolarList = list;

    private void RebuildCalendarDays()
    {
        var today = DateTime.Today;
        CalendarDays.Clear();
        foreach (var date in MonthGridCalculator.BuildDates(CalendarMonth))
        {
            CalendarDays.Add(new KanbanCalendarDayViewModel(
                date,
                isCurrentMonth: date.Month == CalendarMonth.Month,
                isToday: date == today,
                isSelected: date == DueDate.Date));
        }
    }
}
