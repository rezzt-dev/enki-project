using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace enki.Models;

/// <summary>Rol semántico de una columna core del tablero Kanban. Usado por la automatización de
/// subtareas y el dashboard para no depender del orden posicional de las columnas.
/// Las columnas no core siempre tienen <see cref="CoreColumnRole.None"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoreColumnRole
{
    None,
    Backlog,
    InProgress,
    Done
}

public sealed class KanbanColumn
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public bool IsCore { get; set; }

    /// <summary>Rol explícito de la columna dentro del tablero core. Las columnas semilla se crean como
    /// Backlog/InProgress/Done; las columnas personalizadas y los tableros legacy migrados conservan
    /// <see cref="CoreColumnRole.None"/> salvo que se infiera su rol en carga.</summary>
    public CoreColumnRole Role { get; set; } = CoreColumnRole.None;

    public List<KanbanCard> Cards { get; set; } = [];
}
