using System;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public enum ReminderBucket
{
    Overdue,
    Today,
    Soon
}

/// <summary>Which underlying entity a <see cref="ReminderItemViewModel"/> was projected from — the inbox
/// mixes two independent "due soon" sources (docs/agent-docs/notificaciones-y-recordatorios.md) into one
/// bucketed list, and <see cref="ReminderInboxViewModel.OpenCard"/> needs to know which one to jump to
/// (Kanban's board vs. the Recordatorios page).</summary>
public enum ReminderItemSourceKind
{
    KanbanCard,
    Reminder
}

/// <summary>One due-dated item surfaced in the title-bar inbox — a read projection, not a separate
/// persisted entity of its own. Wraps either a <see cref="KanbanCard"/>'s own <see cref="KanbanCard.DueDateUtc"/>
/// or a standalone <see cref="Models.Reminder"/>'s <see cref="Models.Reminder.NotifyAtUtc"/>; dismissing one
/// (see <see cref="ReminderInboxViewModel"/>) doesn't touch the source at all, it just records the dismissal
/// separately in <see cref="Services.DismissedReminderService"/>, keyed by <see cref="SourceId"/>.</summary>
public sealed class ReminderItemViewModel
{
    public ReminderItemSourceKind SourceKind { get; }
    public string SourceId { get; }
    public string Title { get; }
    public string ColumnTitle { get; }
    public DateTime DueDateUtc { get; }
    public ReminderBucket Bucket { get; }

    public string BucketLabel => Bucket switch
    {
        ReminderBucket.Overdue => LocalizationService.Instance["Reminder_Bucket_Overdue"],
        ReminderBucket.Today => LocalizationService.Instance["Reminder_Bucket_Today"],
        _ => LocalizationService.Instance["Reminder_Bucket_Soon"]
    };

    public string DueDateLabel => KanbanCard.FormatDueLabel(DueDateUtc);

    public ReminderItemViewModel(KanbanCard card, string columnTitle)
    {
        if (card.DueDateUtc is not { } due)
            throw new ArgumentException("El card no tiene fecha límite.", nameof(card));

        SourceKind = ReminderItemSourceKind.KanbanCard;
        SourceId = card.Id;
        Title = card.Title;
        ColumnTitle = columnTitle;
        DueDateUtc = due;
        Bucket = BucketFor(due);
    }

    /// <summary>Recordatorio propio (creado desde la página Recordatorios), no una tarjeta Kanban —
    /// <see cref="ColumnTitle"/> se reutiliza como etiqueta de origen (<c>Nav_RemindersLabel</c>, "Recordatorios")
    /// para distinguirlo a simple vista de los proyectados desde el tablero.</summary>
    public ReminderItemViewModel(Models.Reminder reminder)
    {
        SourceKind = ReminderItemSourceKind.Reminder;
        SourceId = reminder.Id;
        Title = reminder.Title;
        ColumnTitle = LocalizationService.Instance["Nav_RemindersLabel"];
        DueDateUtc = reminder.NotifyAtUtc;
        Bucket = BucketFor(reminder.NotifyAtUtc);
    }

    private static ReminderBucket BucketFor(DateTime dueUtc)
    {
        var dueLocalDate = dueUtc.ToLocalTime().Date;
        var today = DateTime.Now.Date;
        return KanbanCard.IsPastDue(dueUtc) ? ReminderBucket.Overdue
            : dueLocalDate == today ? ReminderBucket.Today
            : ReminderBucket.Soon;
    }
}
