using System;
using enki.Localization;
using enki.Models;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Read projection of a persisted <see cref="Reminder"/> for the Recordatorios list/detail dialog.
/// Reuses <see cref="KanbanCard.FormatDueLabel"/>/<see cref="KanbanCard.IsPastDue"/> — the same due-date
/// formatting and overdue cutoff already shared by the Kanban board and the title-bar reminder inbox — so
/// "vencido"/"hoy"/absolute-date rendering reads identically everywhere in the app.</summary>
public sealed class ReminderListItemViewModel(Reminder reminder)
{
    public Reminder Reminder { get; } = reminder;

    public string Id => Reminder.Id;
    public string Title => Reminder.Title;
    public string Description => Reminder.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Reminder.Description);
    public bool IsRead => Reminder.IsRead;
    public bool IsCompleted => Reminder.IsCompleted;
    public MaterialIconKind CompletedToggleIcon => IsCompleted ? MaterialIconKind.CheckCircle : MaterialIconKind.CheckCircleOutline;
    public string CompletedToggleLabel => LocalizationService.Instance[IsCompleted
        ? "Activity_MarkPending" : "Calendar_Item_MarkCompleted"];
    public DateTime NotifyAtUtc => Reminder.NotifyAtUtc;
    public DateTime CreatedUtc => Reminder.CreatedUtc;

    public MaterialIconKind IconKind =>
        Enum.TryParse<MaterialIconKind>(Reminder.Icon, out var kind) ? kind : MaterialIconKind.BellRingOutline;

    public bool IsOverdue => !IsCompleted && KanbanCard.IsPastDue(Reminder.NotifyAtUtc);

    public string StatusLabel => IsCompleted
        ? LocalizationService.Instance["Calendar_Item_Completed"]
        : IsOverdue
        ? LocalizationService.Instance["Reminders_Status_Overdue"]
        : LocalizationService.Instance["Reminders_Status_Scheduled"];

    /// <summary>Compact absolute label for the list row, e.g. "11 AGO 10:30".</summary>
    public string NotifyAtLabel => KanbanCard.FormatDueLabel(Reminder.NotifyAtUtc);

    public string CreatedAtLabel => RelativeTimeFormatter.FormatAbsolute(CreatedUtc);

    /// <summary>Recurrencia local (Fase 10, docs/analisis-proyecto/roadmap.md) — mismo formateador
    /// compartido con la fila de tarea Polar (<see cref="PolarRecurrenceFormatter"/>).</summary>
    public bool HasRecurrence => Reminder.Recurrence != PolarRecurrence.None;
    public string RecurrenceLabel => PolarRecurrenceFormatter.Format(Reminder.Recurrence);
}
