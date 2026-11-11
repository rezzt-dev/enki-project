namespace enki.Models;

/// <summary>Qué hay que deshacer de una acción del asistente.</summary>
public enum AiUndoKind
{
    /// <summary>Cambió el contenido de una nota: se restaura la versión anterior de su historial.</summary>
    NoteEdited,
    /// <summary>Creó una nota (o una copia): se envía a la papelera.</summary>
    NoteCreated,
    /// <summary>Renombró o movió una nota: se devuelve a su ruta anterior.</summary>
    NoteMoved,
    /// <summary>Envió una nota a la papelera: se restaura donde estaba.</summary>
    NoteTrashed,
    KanbanCardCreated,
    /// <summary>Editó, movió o archivó una tarjeta: vuelve a su estado y posición anteriores.</summary>
    KanbanCardChanged,
    KanbanCardTrashed,
    ReminderCreated,
    ReminderChanged,
    ReminderTrashed,
    /// <summary>Marcó o desmarcó un hábito en un día.</summary>
    HabitMarked,
    SafeSettingChanged,
    CalendarLayerChanged
}

public enum AiUndoState
{
    /// <summary>Se puede intentar revertir.</summary>
    Available,
    Reverted,
    /// <summary>Se intentó y no se tocó nada: <see cref="AiToolUndo.Reason"/> dice por qué.</summary>
    Skipped
}

/// <summary>Por qué una acción no se revirtió. Ninguno de estos casos pisa nada: se deja como está.</summary>
public enum AiUndoSkipReason
{
    /// <summary>El elemento ya no está como lo dejó el asistente: alguien lo editó después.</summary>
    ChangedSince,
    /// <summary>El elemento (o el sitio al que tendría que volver) ya no existe.</summary>
    Missing,
    /// <summary>Pertenece a una sesión protegida que está bloqueada.</summary>
    Locked,
    /// <summary>Ya hay otra cosa en el sitio al que tendría que volver.</summary>
    Occupied,
    /// <summary>La versión anterior ya no está en el historial de la nota.</summary>
    HistoryMissing,
    /// <summary>La nota está abierta en el editor con cambios sin guardar o un conflicto pendiente.</summary>
    Busy,
    Failed
}

/// <summary>Lo necesario para deshacer una acción del asistente sin pisar lo que el usuario haya hecho después:
/// dónde quedó el elemento, cómo era antes y la huella del estado en que lo dejó el asistente. Al revertir solo
/// se actúa si el estado actual coincide con esa huella (ver <c>AiVaultToolbox.RevertActivities</c>). De una
/// nota no se guarda el contenido anterior sino el identificador de su versión en el historial de la nota, que
/// vive en el vault y, en una sesión protegida, cifrado como la propia nota.</summary>
public sealed class AiToolUndo
{
    public AiUndoKind Kind { get; set; }

    /// <summary>Dónde quedó el elemento tras la acción: ruta completa de la nota (en la papelera si se borró), o
    /// el identificador de la tarjeta, el recordatorio o el hábito. Local a este equipo, como el historial.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Ruta de la nota antes de renombrarla, moverla o borrarla.</summary>
    public string? PreviousPath { get; set; }

    /// <summary>Versión del historial de la nota con el contenido anterior al cambio.</summary>
    public string? VersionId { get; set; }

    /// <summary>Huella del estado en que el asistente dejó el elemento.</summary>
    public string? ResultHash { get; set; }

    /// <summary>Estado anterior de una tarjeta o un recordatorio (JSON), o el día de un hábito.</summary>
    public string? Previous { get; set; }

    public AiUndoState State { get; set; }

    public AiUndoSkipReason? Reason { get; set; }

    /// <summary>El modelo ya sabe que esta acción se revirtió (se le dice en el turno siguiente).</summary>
    public bool ModelInformed { get; set; }

    /// <summary>Todavía tiene sentido intentarlo: no se ha intentado, o lo que lo impidió es pasajero (la nota
    /// estaba a medio editar, la sesión bloqueada, falló el disco). Lo que cambió después o ya no existe no se
    /// va a poder revertir por mucho que se repita.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanAttempt => State == AiUndoState.Available
        || State == AiUndoState.Skipped && Reason is AiUndoSkipReason.Busy or AiUndoSkipReason.Locked or AiUndoSkipReason.Failed;
}
