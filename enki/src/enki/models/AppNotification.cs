using System;

namespace enki.Models;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>A system-generated event (vault switched, sync finished, an error) — distinct from a task
/// reminder: this is push-driven ("something happened"), reminders are pull-driven ("something is due").
/// See <see cref="KanbanCard.DueDateUtc"/> for the reminder side.</summary>
public sealed class AppNotification
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public string Message { get; set; } = string.Empty;
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
}
