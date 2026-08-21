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

/// <summary>One due-dated <see cref="KanbanCard"/> surfaced in the inbox — a read projection, not a
/// separate persisted entity. Reminders live entirely in the card's own <see cref="KanbanCard.DueDateUtc"/>;
/// dismissing one (see <see cref="ReminderInboxViewModel"/>) doesn't touch the card at all, it just records
/// the dismissal separately in <see cref="Services.DismissedReminderService"/>.</summary>
public sealed class ReminderItemViewModel
{
    public string CardId { get; }
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

        CardId = card.Id;
        Title = card.Title;
        ColumnTitle = columnTitle;
        DueDateUtc = due;

        var dueLocalDate = due.ToLocalTime().Date;
        var today = DateTime.Now.Date;
        Bucket = KanbanCard.IsPastDue(due) ? ReminderBucket.Overdue
            : dueLocalDate == today ? ReminderBucket.Today
            : ReminderBucket.Soon;
    }
}
