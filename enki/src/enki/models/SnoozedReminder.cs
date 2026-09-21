using System;

namespace enki.Models;

/// <summary>De qué entidad real procede un aplazamiento. El aplazamiento nunca copia el dato de origen
/// (ver AGENTS.md, "Unificar sin duplicar"): solo guarda a qué fila apuntar para volver a resolver el
/// título en el momento de reavisar.</summary>
public enum SnoozeSourceKind
{
    /// <summary>Tarjeta del tablero con <see cref="KanbanCard.DueDateUtc"/>.</summary>
    KanbanCard,

    /// <summary>Recordatorio propio de la página Recordatorios (<see cref="Reminder"/>).</summary>
    Reminder
}

/// <summary>Un aviso que el usuario aplazó desde la bandeja de recordatorios o desde el centro de
/// notificaciones (T5 de .docs/funcionalidades-pendientes-priorizadas.md).
///
/// <para>Aplazar <em>no</em> reescribe la entidad de origen: ni la fecha límite de una
/// <see cref="KanbanCard"/> ni el <see cref="Reminder.NotifyAtUtc"/> de un recordatorio —y menos aún la
/// serie de uno recurrente, cuyo <c>NotifyAtUtc</c> ya apunta a la siguiente ocurrencia en cuanto la
/// actual se dispara (ver <c>ReminderService.Fire</c>)—. Un aplazamiento es un "vuelve a avisarme de esto
/// a tal hora", no una reprogramación de la tarea, así que vive aparte en su propio almacén ligero, al
/// lado de <see cref="DismissedReminder"/> y con la misma forma: local, sin viajar nunca por el contrato
/// de sincronización Polar/Supabase.</para>
///
/// <para><see cref="DueUtcSnapshot"/> cumple aquí el mismo papel que
/// <see cref="DismissedReminder.DueDateUtcSnapshot"/>: si la fecha real de la tarjeta o del recordatorio
/// cambia después de aplazarlo, el aplazamiento deja de corresponder a ese aviso y se descarta, en vez de
/// silenciar una fecha nueva que el usuario nunca llegó a ver.</para></summary>
public sealed class SnoozedReminder
{
    public required string SourceId { get; set; }
    public SnoozeSourceKind SourceKind { get; set; }

    /// <summary>Instante (UTC) en el que hay que volver a avisar.</summary>
    public DateTime SnoozedUntilUtc { get; set; }

    /// <summary>Fecha de vencimiento que mostraba el aviso al aplazarlo.</summary>
    public DateTime DueUtcSnapshot { get; set; }

    /// <summary>Aplazamientos consecutivos del mismo aviso. No hay tope (decisión de producto, §9 de
    /// .docs/funcionalidades-pendientes-priorizadas.md: "por defecto solo local y sin límite"), pero sí se
    /// muestra el contador a partir del segundo para que el aplazamiento infinito sea visible.</summary>
    public int Count { get; set; } = 1;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
