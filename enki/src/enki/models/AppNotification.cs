using System;

namespace enki.Models;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>Aviso al que se le puede ofrecer "Aplazar" desde el centro de notificaciones: a qué entidad
/// real apunta y con qué fecha de vencimiento se emitió. Es exactamente lo que
/// <see cref="Services.SnoozedReminderService.Snooze"/> necesita, guardado en el propio aviso para no
/// tener que volver a adivinar el origen a partir del texto (T5).</summary>
/// <param name="Kind">Tarjeta Kanban o recordatorio propio.</param>
/// <param name="SourceId">Id de la tarjeta o del recordatorio.</param>
/// <param name="DueUtc">Fecha de vencimiento que motivó el aviso.</param>
public readonly record struct SnoozeTarget(SnoozeSourceKind Kind, string SourceId, DateTime DueUtc);

/// <summary>A system-generated event (vault switched, sync finished, an error) — distinct from a task
/// reminder: this is push-driven ("something happened"), reminders are pull-driven ("something is due").
/// See <see cref="KanbanCard.DueDateUtc"/> for the reminder side.</summary>
public sealed class AppNotification
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public string Message { get; set; } = string.Empty;
    public LocalizedText? LocalizedTitle { get; set; }
    public LocalizedText? LocalizedMessage { get; set; }
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }

    /// <summary>Enlace que el aviso ofrece abrir en el navegador — hoy solo lo usa el aviso de versión
    /// nueva (T9), con la página de la release. Se vuelve a filtrar por <c>ExternalLinkPolicy</c> al
    /// abrirlo, así que un <c>notifications.json</c> editado a mano no convierte esto en un vector para
    /// abrir cualquier cosa.</summary>
    public string? LinkUrl { get; set; }

    /// <summary>Presente solo en los avisos nacidos de un vencimiento (recordatorio o tarjeta): habilita
    /// el menú "Aplazar" de la fila del centro de notificaciones. Null en todo lo demás —un fallo de
    /// sincronización o un cambio de vault no son cosas que se puedan aplazar.</summary>
    public SnoozeTarget? SnoozeTarget { get; set; }
}
