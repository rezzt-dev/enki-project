using System;

namespace enki.Models;

/// <summary>A user-created reminder, independent of any Kanban card — pull-driven by the user setting a
/// date/time, but push-driven once that time arrives: see <see cref="Services.ReminderService.PopDueReminders"/>,
/// which turns a due reminder into a real <see cref="AppNotification"/>. Also surfaced (merged with Kanban
/// due dates) by the title-bar inbox (<see cref="ViewModels.ReminderInboxViewModel"/>) — that panel is a
/// read projection only, this is the actual persisted entity.</summary>
public sealed class Reminder
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime NotifyAtUtc { get; set; }

    /// <summary>Duración local de la franja horaria en el calendario (minutos). No forma parte del
    /// contrato de sincronización Polar/Supabase documentado en docs/supabase-sync/; se almacena solo en
    /// <c>reminders.json</c>. Permite extender el redimensionado del calendario a recordatorios.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>Recurrencia local del recordatorio (Fase 10, docs/analisis-proyecto/roadmap.md) — reutiliza
    /// el mismo enum que las tareas Polar (<see cref="PolarRecurrence"/>) en vez de introducir un segundo
    /// concepto para la misma idea. Igual que <see cref="DurationMinutes"/>, no forma parte del contrato de
    /// sincronización Polar/Supabase: un recordatorio recurrente creado sin cuenta vinculada (el caso que
    /// motiva esta funcionalidad) nunca necesitó viajar por el wire, y si el recordatorio sí se sincroniza,
    /// solo <see cref="NotifyAtUtc"/> (que sí es un campo real del contrato) se empuja en cada avance —
    /// otro dispositivo lo verá reprogramarse fecha a fecha, pero no sabrá que es recurrente hasta que Polar
    /// añada una columna equivalente. Ver <see cref="Services.ReminderService.PopDueReminders(DateTime)"/>
    /// para el motor de avance, que reutiliza
    /// <see cref="Services.PolarTaskService.CalculateNextDueDate"/> sin duplicar la aritmética de DST.</summary>
    public PolarRecurrence Recurrence { get; set; } = PolarRecurrence.None;

    /// <summary>Name of a <c>Material.Icons.MaterialIconKind</c> value, stored as a string so the model
    /// stays a plain POCO independent of the icon library.</summary>
    public string Icon { get; set; } = "BellRingOutline";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }

    /// <summary>Set once <see cref="NotifyAtUtc"/> has passed and the reminder has already produced its
    /// one-time notification — prevents <see cref="Services.ReminderService.PopDueReminders"/> from firing
    /// the same reminder again on a later poll.</summary>
    public bool IsTriggered { get; set; }

    /// <summary>Evento de calendario importado de ICS. Es local y no programa avisos, incluso
    /// después de moverlo en el calendario. No forma parte del contrato Polar.</summary>
    public bool IsImportedCalendarEvent { get; set; }

    // ===================== Polar-compatible sync fields (docs/supabase-sync/) =====================
    // Icon/IsRead/IsTriggered above are Enki-local UI concerns with no equivalent column in Supabase's
    // `reminders` table — they're simply never sent on the wire. Everything below maps 1:1 to that table
    // (docs/supabase-sync/05-contrato-interoperabilidad.md) so Enki's reminders round-trip with Polar's.

    /// <summary>= Polar's <c>is_completed</c>. Distinct from <see cref="IsRead"/> (an Enki-only "seen it"
    /// flag) and <see cref="IsTriggered"/> (an Enki-only "already notified" flag) — this is the
    /// user-facing "done" state shared with Polar.</summary>
    public bool IsCompleted { get; set; }

    /// <summary>Preserved for compatibility even though Enki has no location-picker UI of its own — see
    /// docs/supabase-sync/05, rule 5 (never drop a field you don't have UI for). Polar itself only uses
    /// these as informational "open in Maps" data, not real geofencing (docs/supabase-sync/02).</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public float? Radius { get; set; }
    public string? LocationName { get; set; }

    /// <summary>Tombstone soft-delete, replacing the previous hard <c>RemoveAll</c> in
    /// <see cref="Services.ReminderService.Delete"/> — see docs/supabase-sync/03/04.</summary>
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }

    /// <summary>Defaulting to "now"/"true" means a reminder loaded from a pre-sync <c>reminders.json</c>
    /// (missing these two JSON keys entirely) naturally becomes dirty and gets pushed on first link,
    /// instead of needing an explicit migration step.</summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool Dirty { get; set; } = true;

    /// <summary>Local-only ownership marker — see <see cref="Models.PolarTaskList.UserId"/> for the full
    /// rationale (docs/analisis-proyecto/roadmap.md, Fase 4). A purely local reminder created without ever
    /// linking a Polar account simply stays null forever, which is fine: it never gets pushed regardless,
    /// since <see cref="Services.PolarSyncService"/> only pushes <see cref="Dirty"/> rows in the first
    /// place, and an unowned row is free to be claimed by whichever account links next.</summary>
    public string? UserId { get; set; }
}
