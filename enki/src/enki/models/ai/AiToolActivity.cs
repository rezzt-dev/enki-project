using System;

namespace enki.Models;

public enum AiToolActivityState
{
    Running,
    Succeeded,
    Failed,
    /// <summary>La acción necesitaba la confirmación del usuario y no la obtuvo (la denegó, detuvo la
    /// respuesta o dejó pasar el tiempo de espera).</summary>
    Denied
}

/// <summary>Qué clase de elemento tocó una acción, para elegir icono y —en notas y tarjetas— poder abrirlo
/// desde el chat.</summary>
public enum AiToolTargetKind
{
    None,
    Note,
    Folder,
    File,
    Session,
    KanbanBoard,
    KanbanCard,
    PolarList,
    PolarTask,
    Reminder,
    Trash
}

/// <summary>Una llamada del asistente a una herramienta del vault (ver <see cref="Services.AiVaultToolbox"/>):
/// crear una nota, mover una tarjeta, buscar… Se guarda en <see cref="AiChatMessage.Activities"/> con el
/// nombre de la herramienta y no con un texto ya traducido, para que la fila se pinte en el idioma vigente
/// cada vez que se abre el historial.</summary>
public sealed class AiToolActivity
{
    /// <summary>Nombre de la herramienta MCP (<c>create_note</c>, <c>move_kanban_card</c>…).</summary>
    public string Tool { get; set; } = string.Empty;

    /// <summary>Etiqueta visible del elemento afectado (título de la nota, de la tarjeta, texto buscado…).
    /// Vacía en las acciones que no apuntan a nada concreto.</summary>
    public string Target { get; set; } = string.Empty;

    public AiToolTargetKind TargetKind { get; set; }

    /// <summary>Para abrir el elemento desde el chat: ruta completa de una nota, identificador de una tarjeta.
    /// Es local a este equipo, igual que el resto del historial.</summary>
    public string? TargetId { get; set; }

    public AiToolActivityState State { get; set; } = AiToolActivityState.Running;

    /// <summary>Motivo del fallo ya traducido, para el usuario. El modelo recibe su propia versión, en
    /// inglés, como resultado de la herramienta.</summary>
    public string? Error { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
