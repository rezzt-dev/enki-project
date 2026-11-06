using System;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Read projection of a persisted <see cref="Reminder"/> for the Recordatorios list/detail dialog.
/// Reuses <see cref="KanbanCard.FormatDueLabel"/>/<see cref="KanbanCard.IsPastDue"/> — the same due-date
/// formatting and overdue cutoff already shared by the Kanban board and the title-bar reminder inbox — so
/// "vencido"/"hoy"/absolute-date rendering reads identically everywhere in the app.</summary>
public sealed partial class ReminderListItemViewModel(Reminder reminder) : ViewModelBase
{
    public Reminder Reminder { get; } = reminder;

    /// <summary>En su ventana de aviso previo y sin ver: lo que pinta el punto de la fila y lo que suma la
    /// insignia de «Recordatorios». Lo fija <see cref="RemindersViewModel"/> con las reglas de
    /// <see cref="ReminderAttention"/>, porque depende de la antelación configurada y del reloj, no solo
    /// del recordatorio.</summary>
    [ObservableProperty]
    private bool _needsAttention;

    /// <summary>Dentro de la ventana de aviso previo pero aún no vencido — «Próximo» en vez de
    /// «Programado», para que la fila diga lo mismo que el aviso emergente que acaba de salir.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private bool _isUpcoming;

    /// <summary>Recalcula el estado que depende del reloj sin reconstruir la fila — la página lo llama
    /// en cada pasada del temporizador de recordatorios, así que «Próximo» y «Vencido» aparecen a su hora
    /// aunque nadie toque la lista.</summary>
    public void UpdateAttention(bool isInWindow, bool needsAttention)
    {
        IsUpcoming = isInWindow && !IsOverdue;
        NeedsAttention = needsAttention;
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(StatusLabel));
    }

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
        : IsUpcoming
        ? LocalizationService.Instance["Reminders_Status_Upcoming"]
        : LocalizationService.Instance["Reminders_Status_Scheduled"];

    /// <summary>Compact absolute label for the list row, e.g. "11 AGO 10:30".</summary>
    public string NotifyAtLabel => KanbanCard.FormatDueLabel(Reminder.NotifyAtUtc);

    public string CreatedAtLabel => RelativeTimeFormatter.FormatAbsolute(CreatedUtc);

    /// <summary>Recurrencia local (Fase 10, docs/analisis-proyecto/roadmap.md) — mismo formateador
    /// compartido con la fila de tarea Polar (<see cref="PolarRecurrenceFormatter"/>).</summary>
    public bool HasRecurrence => Reminder.Recurrence != PolarRecurrence.None;
    public string RecurrenceLabel => PolarRecurrenceFormatter.Format(Reminder.Recurrence);
}
