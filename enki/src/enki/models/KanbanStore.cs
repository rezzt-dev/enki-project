using System.Collections.Generic;

namespace enki.Models;

/// <summary>Almacén versionado de los tableros del vault; las tarjetas conservan su entidad e identidad.</summary>
public sealed class KanbanStore
{
    public int SchemaVersion { get; set; } = 1;
    public string DefaultBoardId { get; set; } = string.Empty;
    public List<KanbanBoard> Boards { get; set; } = [];
}

/// <summary>Referencias a la ubicación real de una tarjeta, sin duplicar datos persistidos.</summary>
public sealed record KanbanCardLocation(KanbanBoard Board, KanbanColumn Column, KanbanCard Card);
