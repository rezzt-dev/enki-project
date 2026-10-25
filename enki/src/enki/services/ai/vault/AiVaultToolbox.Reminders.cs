using System;
using System.Linq;
using enki.Models;
using enki.ViewModels;

namespace enki.Services;

/// <summary>Recordatorios (<see cref="ReminderService"/>) y la agenda que el Calendario monta con todo lo que
/// tiene fecha (<see cref="CalendarAggregationService"/>): tarjetas, tareas de Polar, recordatorios y
/// calendarios externos suscritos, sin una copia propia de nada.</summary>
public sealed partial class AiVaultToolbox
{
    private const int MaxAgendaDays = 62;

    private void RegisterReminderTools()
    {
        Register("list_reminders", AiToolScope.Vault, AiToolEffect.Read,
            "Lists reminders (they notify the user at a given time), soonest first. Optionally only those between 'from' and 'to'.",
            Schema(Opt("from", Text("From this date. " + DateDescription)), Opt("to", Text("Until this date. " + DateDescription)),
                Opt("include_completed", Flag("Include completed reminders (default true)."))),
            call =>
            {
                var from = Date(call, "from");
                var to = Date(call, "to");
                var completed = call.GetFlag("include_completed") ?? true;
                return new
                {
                    Reminders = _reminders.ActiveReminders
                        .Where(r => (from is null || r.NotifyAtUtc >= from) && (to is null || r.NotifyAtUtc <= to) && (completed || !r.IsCompleted))
                        .OrderBy(r => r.NotifyAtUtc)
                        .Take(MaxListLimit)
                        .Select(ReminderSummary)
                        .ToList()
                };
            });

        Register("create_reminder", AiToolScope.Vault, AiToolEffect.Change,
            "Creates a reminder that notifies the user at 'notify_at' (local time), optionally recurring.",
            Schema(Req("title", Text("Reminder title.")), Req("notify_at", Text("When to notify. " + DateDescription)),
                Opt("description", Text("Details.")), Opt("duration_minutes", Number("Calendar time slot length.")),
                Opt("recurrence", Choice("Recurrence (default none).", RecurrenceNames))),
            call =>
            {
                var title = call.RequireText("title").Trim();
                call.Target(AiToolTargetKind.Reminder, title);
                var notifyAt = Date(call, "notify_at") ?? throw AiToolCall.Invalid("'notify_at' is required.");
                var reminder = _reminders.Create(title, call.GetText("description") ?? string.Empty, notifyAt, "BellRingOutline",
                    call.GetInteger("duration_minutes", 0, 24 * 60), Recurrence(call) ?? PolarRecurrence.None);
                call.Target(AiToolTargetKind.Reminder, reminder.Title, reminder.Id);
                return ReminderSummary(reminder);
            });

        Register("update_reminder", AiToolScope.Vault, AiToolEffect.Change,
            "Reschedules a reminder (it will notify again at the new time), changes its calendar duration (null clears it) "
            + "or marks it completed or pending.",
            Schema(Req("reminder_id", Text("Reminder id.")), Opt("notify_at", Text("New time. " + DateDescription)),
                Opt("duration_minutes", new System.Text.Json.Nodes.JsonObject { ["type"] = new System.Text.Json.Nodes.JsonArray("integer", "null") }),
                Opt("completed", Flag("Completed state."))),
            call =>
            {
                var reminder = Reminder(call);
                if (Date(call, "notify_at") is { } notifyAt) _reminders.Reschedule(reminder.Id, notifyAt);
                if (call.Has("duration_minutes")) _reminders.RescheduleDuration(reminder.Id, call.GetInteger("duration_minutes", 0, 24 * 60));
                if (call.GetFlag("completed") is { } completed && completed != reminder.IsCompleted) _reminders.ToggleCompleted(reminder.Id);
                return ReminderSummary(reminder);
            });

        Register("delete_reminder", AiToolScope.Vault, AiToolEffect.Change,
            "Moves a reminder to the trash.",
            Schema(Req("reminder_id", Text("Reminder id."))),
            call =>
            {
                var reminder = Reminder(call);
                _reminders.Delete(reminder.Id);
                return new { Trashed = true, reminder.Title };
            });

        Register("get_agenda", AiToolScope.Vault, AiToolEffect.Read,
            "Everything with a date between 'from' and 'to', as in the Calendar section: Kanban cards, Polar tasks and subtasks, "
            + $"reminders and subscribed external calendars, including upcoming occurrences of recurring tasks. Up to {MaxAgendaDays} days.",
            Schema(Req("from", Text("Start date. " + DateDescription)), Opt("to", Text("End date (default: 7 days after 'from'). " + DateDescription))),
            call =>
            {
                if (_calendar is null) throw new AiToolException(AiToolErrorKind.Unavailable, "The agenda is not available.");
                var from = Date(call, "from") ?? throw AiToolCall.Invalid("'from' is required.");
                var to = Date(call, "to") ?? from.AddDays(7);
                if (to <= from) throw AiToolCall.Invalid("'to' must be after 'from'.");
                if (to - from > TimeSpan.FromDays(MaxAgendaDays)) throw AiToolCall.Invalid($"The range can be at most {MaxAgendaDays} days.");
                return new
                {
                    From = FormatDate(from),
                    To = FormatDate(to),
                    Items = _calendar.BuildItems(from, to).Select(item => new
                    {
                        Type = item.SourceType switch
                        {
                            CalendarItemSourceType.Kanban => "kanban_card",
                            CalendarItemSourceType.PolarTask => "polar_task",
                            CalendarItemSourceType.PolarSubtask => "polar_subtask",
                            CalendarItemSourceType.Reminder => "reminder",
                            _ => "external_event"
                        },
                        Id = item.SourceId,
                        item.Title,
                        When = item.IsAllDay ? FormatDate(item.MomentUtc.ToLocalTime().Date.ToUniversalTime()) : FormatInstant(item.MomentUtc),
                        AllDay = item.IsAllDay,
                        item.DurationMinutes,
                        Start = item.IsOpeningMilestone ? true : (bool?)null,
                        item.IsCompleted,
                        UpcomingOccurrence = item.IsGhostOccurrence ? true : (bool?)null
                    }).ToList()
                };
            });
    }

    private Reminder Reminder(AiToolCall call)
    {
        var id = call.RequireText("reminder_id").Trim();
        var reminder = _reminders.ActiveReminders.FirstOrDefault(r => r.Id == id)
            ?? throw new AiToolException(AiToolErrorKind.NotFound, $"There is no reminder with id \"{id}\". Use list_reminders.");
        call.Target(AiToolTargetKind.Reminder, reminder.Title, reminder.Id);
        return reminder;
    }

    private static object ReminderSummary(Reminder reminder) => new
    {
        reminder.Id,
        reminder.Title,
        Description = reminder.Description.Length > 0 ? Truncate(reminder.Description, 2_000) : null,
        NotifyAt = FormatInstant(reminder.NotifyAtUtc),
        reminder.DurationMinutes,
        Recurrence = reminder.Recurrence == PolarRecurrence.None ? null : RecurrenceName(reminder.Recurrence),
        Completed = reminder.IsCompleted,
        Notified = reminder.IsTriggered,
        ImportedEvent = reminder.IsImportedCalendarEvent ? true : (bool?)null
    };
}
