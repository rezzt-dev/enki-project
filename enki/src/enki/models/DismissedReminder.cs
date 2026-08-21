using System;

namespace enki.Models;

/// <summary>One reminder the user dismissed from the inbox panel — snapshots the due date that was
/// showing at dismiss time (see docs/notificaciones-y-recordatorios.md): a flat "seen"
/// flag keyed only by card id would silently hide a card forever even after its due date is rescheduled,
/// so the snapshot lets the inbox tell "still the same reminder I dismissed" apart from "this looks like
/// the same card but the date changed, so it's a new reminder" and resurface only the latter.</summary>
public sealed class DismissedReminder
{
    public required string CardId { get; set; }
    public DateTime DueDateUtcSnapshot { get; set; }
}
