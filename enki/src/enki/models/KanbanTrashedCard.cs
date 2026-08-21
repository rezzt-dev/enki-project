using System;

namespace enki.Models;

public sealed class KanbanTrashedCard
{
    public required KanbanCard Card { get; set; }
    public required string ColumnId { get; set; }
    public required string ColumnTitle { get; set; }
    public DateTime DeletedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Índice original de la tarjeta dentro de <see cref="KanbanColumn.Cards"/> en el momento
    /// de borrarla, para poder restaurarla en la misma posición. <c>null</c> para tarjetas borradas antes
    /// de esta mejora o cuando la columna original ya no existe.</summary>
    public int? CardIndex { get; set; }
}
