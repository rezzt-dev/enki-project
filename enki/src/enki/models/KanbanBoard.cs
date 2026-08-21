using System.Collections.Generic;

namespace enki.Models;

public sealed class KanbanBoard
{
    public required string Name { get; set; }
    public List<KanbanColumn> Columns { get; set; } = [];
    public List<KanbanTrashedCard> TrashedCards { get; set; } = [];
}
