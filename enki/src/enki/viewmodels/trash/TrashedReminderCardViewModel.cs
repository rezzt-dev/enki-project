using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public sealed class TrashedReminderCardViewModel(Reminder reminder)
{
    public Reminder Reminder { get; } = reminder;
    public string Title => Reminder.Title;

    public string DeletedLabel => Reminder.DeletedAtUtc is { } deletedUtc
        ? RelativeTimeFormatter.Format(deletedUtc)
        : string.Empty;
}
