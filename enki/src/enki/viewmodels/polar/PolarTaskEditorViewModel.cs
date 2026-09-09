using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public enum PolarTaskEditorMode
{
    Create,
    Edit
}

/// <summary>Draft state backing the create/edit task popup. Title/description/priority/due date/
/// recurrence/time estimate/tags are staged here and only written to the model on "Guardar", same as
/// <see cref="KanbanCardEditorViewModel"/> — but subtasks are the one exception: see
/// <see cref="PolarSubtaskRowViewModel"/> for why those write through immediately instead of being staged
/// (only available once editing an existing task, i.e. after it has an id to attach to).</summary>
public sealed partial class PolarTaskEditorViewModel : ObservableObject
{
    public const int DescriptionMaxLength = 240;

    private readonly PolarTaskService? _taskService;

    public PolarTaskEditorMode Mode { get; }
    public bool IsCreateMode => Mode == PolarTaskEditorMode.Create;
    public PolarTaskList TargetList { get; }
    public PolarTask? SourceTask { get; }

    /// <summary>Only populated in edit mode — see the class remarks on why subtasks aren't staged.</summary>
    public ObservableCollection<PolarSubtaskRowViewModel> Subtasks { get; } = [];

    public ObservableCollection<KanbanCalendarDayViewModel> CalendarDays { get; } = [];

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private int _priority;

    [ObservableProperty]
    private PolarRecurrence _recurrence = PolarRecurrence.None;

    [ObservableProperty]
    private int _timeEstimate;

    [ObservableProperty]
    private string _tagsInput = string.Empty;

    [ObservableProperty]
    private DateTime? _dueDate;

    [ObservableProperty]
    private bool _hasDueTime;

    [ObservableProperty]
    private int _dueHour = 9;

    [ObservableProperty]
    private int _dueMinute;

    [ObservableProperty]
    private bool _isCalendarOpen;

    [ObservableProperty]
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty]
    private string _newSubtaskTitle = string.Empty;

    public string DescriptionCounterLabel => $"{Description.Length}/{DescriptionMaxLength}";
    public bool CanSave => !string.IsNullOrWhiteSpace(Title);
    public bool HasDueDate => DueDate is not null;

    public string DueDateDisplayLabel => DueDate is { } due
        ? (HasDueTime
            ? $"{due.ToString("d", CultureInfo.CurrentCulture)} {DueHour:00}:{DueMinute:00}"
            : due.ToString("d", CultureInfo.CurrentCulture))
        : LocalizationService.Instance["Polar_TaskEditor_SelectDate"];

    /// <summary>Built from <see cref="CultureInfo.CurrentCulture"/> instead of a hardcoded Spanish month
    /// array (docs/agent-docs/internacionalizacion-i18n.md §1.7) — uppercased to keep this label's existing
    /// all-caps chip style regardless of what casing <see cref="CultureInfo.DateTimeFormat"/> returns.</summary>
    public string CalendarMonthLabel =>
        $"{CultureInfo.CurrentCulture.TextInfo.ToUpper(CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(CalendarMonth.Month))} {CalendarMonth.Year}";

    public string HeadingEyebrow => IsCreateMode
        ? LocalizationService.Instance["Polar_TaskEditor_NewEyebrow"]
        : LocalizationService.Instance["Polar_TaskEditor_EditEyebrow"];
    public string HeadingTitle => IsCreateMode
        ? LocalizationService.Instance["Polar_TaskEditor_CreateTitle"]
        : LocalizationService.Instance["Polar_TaskEditor_EditTitle"];
    public string ConfirmLabel => IsCreateMode
        ? LocalizationService.Instance["Polar_Editor_ConfirmCreate"]
        : LocalizationService.Instance["Polar_Editor_ConfirmSave"];

    /// <summary>Un editor abierto sobrevive a ir a Ajustes y volver, y <c>Reload()</c> no lo reconstruye
    /// para no descartar lo escrito — así que sus etiquetas se refrescan en el sitio desde
    /// <see cref="PolarTaskManagementViewModel.RefreshLocalizedLabels"/>.</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(HeadingEyebrow));
        OnPropertyChanged(nameof(HeadingTitle));
        OnPropertyChanged(nameof(ConfirmLabel));
        OnPropertyChanged(nameof(DueDateDisplayLabel));
        OnPropertyChanged(nameof(CalendarMonthLabel));
    }

    private DateTime? CombinedDueDate => DueDate is { } date
        ? (HasDueTime ? date.Date.Add(new TimeSpan(DueHour, DueMinute, 0)) : date.Date)
        : null;

    private PolarTaskEditorViewModel(PolarTaskEditorMode mode, PolarTaskList targetList, PolarTask? sourceTask, PolarTaskService? taskService)
    {
        Mode = mode;
        TargetList = targetList;
        SourceTask = sourceTask;
        _taskService = taskService;

        RebuildCalendarDays();
    }

    public static PolarTaskEditorViewModel ForCreate(PolarTaskList list) =>
        new(PolarTaskEditorMode.Create, list, null, null);

    public static PolarTaskEditorViewModel ForEdit(PolarTaskList list, PolarTask task, PolarTaskService taskService)
    {
        var dueLocal = task.DueDateUtc?.ToLocalTime();
        var dueDate = dueLocal?.Date;
        var hasDueTime = dueLocal is { } dl && dl.TimeOfDay != TimeSpan.Zero;

        var editor = new PolarTaskEditorViewModel(PolarTaskEditorMode.Edit, list, task, taskService)
        {
            Title = task.Title,
            Description = task.Description,
            Priority = task.Priority,
            Recurrence = task.Recurrence,
            TimeEstimate = task.TimeEstimate,
            TagsInput = string.Join(", ", task.Tags),
            DueDate = dueDate,
            HasDueTime = hasDueTime,
            DueHour = hasDueTime ? dueLocal!.Value.Hour : 9,
            DueMinute = hasDueTime ? dueLocal!.Value.Minute : 0
        };

        if (dueDate is { } due) editor.CalendarMonth = new DateTime(due.Year, due.Month, 1);
        editor.ReloadSubtasks();

        return editor;
    }

    private void ReloadSubtasks()
    {
        Subtasks.Clear();
        if (_taskService is null || SourceTask is null) return;

        foreach (var subtask in _taskService.Store.Subtasks
                     .Where(s => s.TaskId == SourceTask.Id && !s.IsDeleted)
                     .OrderBy(s => s.OrderIndex))
        {
            Subtasks.Add(new PolarSubtaskRowViewModel(_taskService, subtask));
        }
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(DescriptionCounterLabel));

    partial void OnNewSubtaskTitleChanged(string value) => AddSubtaskCommand.NotifyCanExecuteChanged();

    partial void OnDueDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(HasDueDate));
        OnPropertyChanged(nameof(DueDateDisplayLabel));
        RebuildCalendarDays();
    }

    partial void OnHasDueTimeChanged(bool value) => OnPropertyChanged(nameof(DueDateDisplayLabel));

    partial void OnDueHourChanged(int value) => OnPropertyChanged(nameof(DueDateDisplayLabel));

    partial void OnDueMinuteChanged(int value) => OnPropertyChanged(nameof(DueDateDisplayLabel));

    /// <summary>Same [0,23]/[0,59] stepper commands as <see cref="KanbanCardEditorViewModel"/>'s due-time
    /// stepper — both editors now share the same "time-stepper" control (KanbanView.axaml), so the
    /// clamping behavior behind it must match too.</summary>
    [RelayCommand]
    private void IncrementDueHour() => DueHour = Math.Min(DueHour + 1, 23);

    [RelayCommand]
    private void DecrementDueHour() => DueHour = Math.Max(DueHour - 1, 0);

    [RelayCommand]
    private void IncrementDueMinute() => DueMinute = Math.Min(DueMinute + 5, 59);

    [RelayCommand]
    private void DecrementDueMinute() => DueMinute = Math.Max(DueMinute - 5, 0);

    partial void OnCalendarMonthChanged(DateTime value)
    {
        OnPropertyChanged(nameof(CalendarMonthLabel));
        RebuildCalendarDays();
    }

    [RelayCommand]
    private void SelectPriority(string priorityText)
    {
        if (int.TryParse(priorityText, out var priority)) Priority = priority;
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
            var anchor = DueDate ?? DateTime.Today;
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
        DueDate = day.Date;
        IsCalendarOpen = false;
    }

    [RelayCommand]
    private void SetDueDateToday()
    {
        CalendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        DueDate = DateTime.Today;
    }

    [RelayCommand]
    private void ClearDueDate()
    {
        DueDate = null;
        HasDueTime = false;
    }

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
                isSelected: DueDate is { } due && due.Date == date));
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddSubtask))]
    private void AddSubtask()
    {
        var title = NewSubtaskTitle.Trim();
        if (title.Length == 0 || _taskService is null || SourceTask is null) return;

        var subtask = _taskService.AddSubtask(SourceTask, title);
        Subtasks.Add(new PolarSubtaskRowViewModel(_taskService, subtask));
        NewSubtaskTitle = string.Empty;
    }

    private bool CanAddSubtask() => !string.IsNullOrWhiteSpace(NewSubtaskTitle) && SourceTask is not null;

    [RelayCommand]
    private void RemoveSubtask(PolarSubtaskRowViewModel item)
    {
        if (_taskService is null) return;

        _taskService.DeleteSubtask(item.Subtask);
        Subtasks.Remove(item);
    }

    public string[] BuildTags() => TagsInput
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public DateTime? BuildDueDateUtc() => CombinedDueDate is { } local
        ? DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime()
        : null;
}
